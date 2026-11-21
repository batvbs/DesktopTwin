using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

internal enum UuViewTarget
{
    Host,
    Child
}

/// <summary>
/// UU 远程的「视角切换」：在「UU 控制整台电脑」和「UU 控制桌面分身」之间来回切。
///
/// 原理（和 UU视角切换.ps1 一致，结论来自实测）：
/// UU 的被控端 <c>GameViewerServer.exe</c> 由 SYSTEM 服务拉起，服务只会挑
/// 「会话号最大的、已登录的桌面」。所以只要改变「哪些会话还活着」，
/// 再把这个串流端结束掉，服务重连时就会落到另一个会话：
///
/// - 去整台电脑：断开分身会话 → 剩下的已登录桌面只有 Console → 串流端只能落 Console
/// - 回分身：把分身连回来 → 分身会话号更大 → 串流端落回分身
///
/// 断开不等于注销：分身的程序都还在跑，来回切不需要重开游戏。
/// </summary>
internal sealed class UuViewSwitchService : IDisposable
{
    private const string StreamerProcessName = "GameViewerServer";

    /// <summary>
    /// UU 的系统服务名。切换后手机显示离线时，重启它可以恢复。
    /// </summary>
    private const string StreamerServiceName = "GameViewerService";

    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StreamerSettleWait = TimeSpan.FromSeconds(30);

    private readonly ChildSessionService _childSessionService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastSwitchFinishedAt = DateTime.MinValue;

    /// <summary>最近几秒内刚有一次切换完成。视角锁定用它跳过「切换落定但记录值未同步」的间隙轮。</summary>
    internal bool IsSwitchRecentlyFinished =>
        (DateTime.UtcNow - _lastSwitchFinishedAt).TotalSeconds < 3;
    private CancellationTokenSource? _streamRecoveryCancellation;
    private bool _killedStreamersInLastSwitch;

    /// <summary>正在跑的后台恢复任务，切换前要等它收手。</summary>
    private Task? _recoveryTask;

    private readonly ServiceRecovery _serviceRecovery;
    private bool _disposed;

    /// <summary>
    /// 创建切换服务。
    /// </summary>
    /// <param name="childSessionService">用于断开/重连分身。</param>
    public UuViewSwitchService(ChildSessionService childSessionService)
    {
        _childSessionService = childSessionService;
        _serviceRecovery = new ServiceRecovery(
            () => ServiceNativeMethods.TryQueryState(StreamerServiceName, out var state) ? state : null,
            () => RequestService(start: false),
            () => RequestService(start: true));
    }

    /// <summary>
    /// 串流端在我们结束它之后迟迟没有回来、又被自动恢复成功。
    /// </summary>
    public event EventHandler? StreamerRecovered;

    /// <summary>
    /// 一次切换的结果，用于界面提示。
    /// </summary>
    /// <param name="Success">是否切换成功。</param>
    /// <param name="Message">给用户看的一句话说明。</param>
    /// <param name="Target">成功切换后的逻辑目标，避免调用方读取共享的上次结果。</param>
    public readonly record struct Result(bool Success, string Message, UuViewTarget? Target = null);

    /// <summary>
    /// 在「整台电脑」与「桌面分身」之间切换一次。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public Task<Result> ToggleAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(token => SwitchAsync(null, token), cancellationToken);

    internal Task<Result> EnsureTargetAsync(UuViewTarget target, CancellationToken cancellationToken) =>
        ExecuteAsync(token => SwitchAsync(target, token), cancellationToken);

    internal static UuViewTarget CaptureTarget()
    {
        var streamers = GetStreamerSessionIds();
        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        return streamers.Count == 0 || streamers[0] == console ? UuViewTarget.Host : UuViewTarget.Child;
    }

    internal static bool NeedsCorrection(UuViewTarget target)
    {
        var session = target == UuViewTarget.Host
            ? ChildSessionNativeMethods.TryGetConsoleSessionId()
            : ChildSessionNativeMethods.TryGetChildSessionId();
        var streamers = GetStreamerSessionIds();
        return session is { } id && streamers.Count > 0 && !streamers.Contains(id);
    }

    private async Task<Result> SwitchAsync(UuViewTarget? requestedTarget, CancellationToken cancellationToken)
    {
        var automatic = requestedTarget.HasValue;
        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        var streamers = GetStreamerSessionIds();
        var child = ChildSessionNativeMethods.TryGetChildSessionId();
        var target = requestedTarget ?? (
            (console is { } host && streamers.Contains(host))
            || (streamers.Count == 0 && child is { } id && ChildSessionNativeMethods.IsSessionActive(id))
                ? UuViewTarget.Child : UuViewTarget.Host);

        // 闸门内复查：等恢复收尾期间串流端可能已回到目标，或 UU 已断开。
        if (automatic && !NeedsCorrection(target))
        {
            return new Result(true, string.Empty, target);
        }

        AppLog.Info($"UU 视角切换：目标={target} 自动={automatic} 串流端=[{string.Join(',', streamers)}]");
        var result = target == UuViewTarget.Host
            ? await SwitchToMainAsync(cancellationToken, restoreChildConnection: !automatic).ConfigureAwait(false)
            : await SwitchToChildAsync(cancellationToken, existingSessionOnly: automatic).ConfigureAwait(false);
        return result with { Target = result.Success ? target : null };
    }

    /// <summary>切换、纠正及手动修复共用互斥、会话取消边界与服务恢复收尾。</summary>
    private async Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var operation = _childSessionService.BeginSessionOperation(cancellationToken);
        cancellationToken = operation.Token;
        var acquired = false;
        try
        {
            await _childSessionService.WaitForSessionEndAsync(cancellationToken).ConfigureAwait(false);
            acquired = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!acquired) return new Result(false, Locale.Get("Uu.Result.Busy"));

            await StopStreamerRecoveryAsync().ConfigureAwait(false);
            if (!await _serviceRecovery.WaitForIdleAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false))
            {
                return new Result(false, Locale.Get("Uu.Result.ServiceRecoveryPending"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new Result(false, Locale.Get("Uu.Result.ServiceRecoveryPending"));
        }
        catch (OperationCanceledException)
        {
            return new Result(false, Locale.Get("Uu.Result.Cancelled"));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("UU 视角切换失败。", exception);
            return new Result(false, Locale.Format("Uu.Result.Failed", exception.GetBaseException().Message));
        }
        finally
        {
            if (acquired)
            {
                // 恢复的排入和切换使用同一把闸门，不再用脱离闸门的延迟续体补排。
                try { ScheduleStreamerRecovery(); }
                finally
                {
                    _lastSwitchFinishedAt = DateTime.UtcNow;
                    _gate.Release();
                }
            }
        }
    }

    /// <summary>
    /// 串流端是否正在被自动恢复（供界面显示状态）。
    /// </summary>
    public bool IsStreamerRecoveryRunning => _recoveryTask is { IsCompleted: false } || _serviceRecovery.IsBusy;

    /// <summary>
    /// 启动（或重启）串流端的后台恢复循环。
    ///
    /// 只在「这次切换确实结束过串流端」时才启动：手机没连着的时候本来就没有串流端，
    /// 那种情况下重启 UU 服务纯属添乱。
    /// </summary>
    private void ScheduleStreamerRecovery()
    {
        if (_disposed) return;
        StopStreamerRecovery();
        if (GetStreamerSessionIds().Count > 0)
        {
            _killedStreamersInLastSwitch = false;
            return;
        }
        if (!_killedStreamersInLastSwitch) return;

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _streamRecoveryCancellation = cancellation;
        if (_disposed)
        {
            StopStreamerRecovery();
            return;
        }
        _recoveryTask = Task.Run(() => RecoverStreamerAsync(token));
    }

    // 调用方持有 _gate。取消等待后，已接受的 SCM 命令仍由 ServiceRecovery 跟踪。
    private async Task StopStreamerRecoveryAsync()
    {
        StopStreamerRecovery();
        if (_recoveryTask is not null) await _recoveryTask.ConfigureAwait(false);
        _recoveryTask = null;
    }

    /// <summary>
    /// 停止后台恢复循环（切换开始时、退出时调用）。
    /// </summary>
    public void StopStreamerRecovery()
    {
        var cancellation = Interlocked.Exchange(ref _streamRecoveryCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    /// <summary>
    /// 后台恢复循环：定时检查串流端，没回来就尝试拉起 UU 服务，直到成功或被取消。
    ///
    /// 节奏：先等 15 秒（手机端重连通常几秒内完成），之后逐步拉长到 60 秒；
    /// 每 4 次做一次完整的「停止 + 启动」，其余时候只发 start（服务在跑时是空操作）。
    /// 不设总时长上限——用户的要求是「直到成功」；但频率被压到最低，不会成为负担。
    /// </summary>
    private async Task RecoverStreamerAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = TimeSpan.FromSeconds(15);
        try
        {
            while (true)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds + 10));
                attempt++;

                Task<ServiceRecovery.Outcome> recovery;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (GetStreamerSessionIds().Count > 0)
                    {
                        FinishRecovery(attempt);
                        return;
                    }

                    AppLog.Warn($"UU 串流端仍未拉起，第 {attempt} 次尝试恢复 UU 服务。");
                    recovery = _serviceRecovery.EnsureRunningAsync(attempt % 4 == 0, cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }

                // 只取消本轮等待；在途的 stop/start 仍保留在 ServiceRecovery 中。
                await recovery.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("UU 串流自动恢复循环异常结束。", exception);
        }
    }

    /// <summary>
    /// 结束恢复义务。
    ///
    /// 不清的话，下次「没杀到串流端」的切换又会触发恢复，而恢复每四轮就重启一次服务，
    /// 平白打断正在用的串流。
    /// </summary>
    private void FinishRecovery(int attempt)
    {
        AppLog.Info($"UU 串流端已恢复，停止自动恢复（共检查 {attempt} 次）。");
        _killedStreamersInLastSwitch = false;
        RaiseStreamerRecovered();
    }

    private void RaiseStreamerRecovered()
    {
        try
        {
            StreamerRecovered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or ObjectDisposedException)
        {
            AppLog.Warn("通知「串流已恢复」失败。", exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopStreamerRecovery();
        // 在途操作仍会释放闸门；无需创建 WaitHandle 的信号量交由 GC 回收。
    }

    /// <summary>
    /// 重启 UU 的串流服务，用于「切换后手机显示设备离线」时的人工恢复。
    ///
    /// 背景：切换的杠杆就是结束 GameViewerServer，让服务重新挑会话。短时间内连续切换
    /// （实测两分钟内四次）会让服务把它当成「连续崩溃」而进入重启退避，之后就不再拉起
    /// 串流端——手机上看到的就是「设备离线」。重启服务可以清掉这个状态。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public Task<Result> RestartStreamerServiceAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async token =>
        {
            var outcome = await _serviceRecovery.EnsureRunningAsync(restart: true, token)
                .WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (outcome != ServiceRecovery.Outcome.Succeeded)
            {
                return new Result(false, Locale.Get(outcome == ServiceRecovery.Outcome.StopFailed
                    ? "Uu.Result.ServiceStopFailed" : "Uu.Result.ServiceStartFailed"));
            }

            var settled = await WaitUntilAsync(() => GetStreamerSessionIds().Count > 0,
                TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            return new Result(true, Locale.Get(settled
                ? "Uu.Result.ServiceRestarted" : "Uu.Result.ServiceRestarted.NoStreamer"));
        }, cancellationToken);

    private static bool RequestService(bool start)
    {
        string error;
        var accepted = start
            ? ServiceNativeMethods.TryStart(StreamerServiceName, out error, waitForState: false)
            : ServiceNativeMethods.TryStop(StreamerServiceName, out error, waitForState: false);
        if (!accepted) AppLog.Warn(error);
        return accepted;
    }

    /// <summary>
    /// 切到「整台电脑」：断开分身，逼 UU 落到 Console。
    /// </summary>
    private async Task<Result> SwitchToMainAsync(
        CancellationToken cancellationToken,
        bool restoreChildConnection = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var childSession = ChildSessionNativeMethods.TryGetChildSessionId();

        // 先让 RDP 客户端自己断开：这条路径是「有意的断开」，
        // 不会触发「RDP 连接意外断开」的失败提示。
        _ = _childSessionService.DisconnectDesktopForSwitch(cancellationToken);

        if (childSession is { } sessionId)
        {
            var disconnected = await WaitUntilAsync(
                () => !ChildSessionNativeMethods.IsSessionActive(sessionId),
                DisconnectWait,
                cancellationToken).ConfigureAwait(false);

            if (!disconnected)
            {
                // 客户端断开没生效时，从 WTS 层面补一刀。
                _ = ChildSessionNativeMethods.DisconnectSession(sessionId);
                disconnected = await WaitUntilAsync(
                    () => !ChildSessionNativeMethods.IsSessionActive(sessionId),
                    DisconnectWait,
                    cancellationToken).ConfigureAwait(false);

                if (!disconnected)
                {
                    return new Result(false, Locale.Get("Uu.Result.DisconnectFailed"));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        var killed = KillStreamers(out var killFailed);

        // 只有真的杀掉了才置位。写成 killed > 0 会把上一轮的义务覆盖成 false：
        // 那时进程已经杀光、只是还没回来，一次新的切换就把恢复丢了。
        if (killed > 0)
        {
            _killedStreamersInLastSwitch = true;
        }

        if (killFailed)
        {
            // 有进程没杀掉：哪怕同时杀掉了别的，留下的那个也可能还挂在原来的会话上。
            // 这种情况不能报成功。
            //
            // 但分身在上一步已经被断开了：不连回来，用户会盯着一块黑屏，
            // 而程序以为还停在分身里。先试着恢复连接，失败也要写在结论里。
            if (childSession is null)
            {
                // 本来就没有分身（用户是在主桌面上做的这次切换）：这里再走一次连接流程
                // 会顺手把分身建出来，等于用一次失败的切换把用户塞进分身里。
                return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed"));
            }

            // 视角锁定触发的纠正不重连：那可能是用户刚注销的分身，连回来会自作主张。
            if (!restoreChildConnection)
            {
                return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed.NotRestored"));
            }

            // 这次切换已经失败，用户还停在分身里：连回来就行，别再把他看着的窗口缩下去。
            var childRestored = await _childSessionService
                .ConnectDesktopInBackgroundAsync(cancellationToken)
                .ConfigureAwait(false);

            return new Result(
                false,
                Locale.Get(childRestored
                    ? "Uu.Result.StreamerKillFailed"
                    : "Uu.Result.StreamerKillFailed.NotRestored"));
        }

        if (killed == 0)
        {
            // 手机没连着的时候本来就没有串流端，分身已经断开，目的已经达到。
            return new Result(true, Locale.Get("Uu.Result.ToMain.NoStreamer"));
        }

        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        var settled = console is { } consoleId
            && await WaitUntilAsync(
                () => GetStreamerSessionIds().Contains(consoleId),
                StreamerSettleWait,
                cancellationToken).ConfigureAwait(false);

        if (!settled)
        {
            return new Result(false, Locale.Get("Uu.Result.ToMain.StreamerStuck"));
        }

        // 串流端已经在主桌面上了，这时候把分身挂回后台不会影响 UU 的选择；
        // 而下次切回分身就是秒切，不用重新走一遍连接握手。
        // 对应原版脚本的 keep_child_connected。
        //
        // 视角锁定触发的纠正不这么做：那边可能是用户刚注销掉分身，把他关掉的会话
        // 又连回来会让人以为程序在自作主张。
        if (!restoreChildConnection)
        {
            return new Result(true, Locale.Get("Uu.Result.ToMain"));
        }

        var restored = await _childSessionService
            .ConnectDesktopInBackgroundAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Result(
            true,
            restored
                ? Locale.Get("Uu.Result.ToMain")
                : Locale.Get("Uu.Result.ToMain.NotRestored"));
    }

    /// <summary>
    /// 切回「桌面分身」：把分身连回来，逼 UU 落到分身。
    ///
    /// 步骤对齐社区脚本：先查**会话状态**，再决定要不要重建，最后才弹串流端。
    /// 不能因为「后台还挂着连接」就跳过检查——RDP 的 Connected 属性和会话状态
    /// 都可能滞后，后台挂着一条断掉的连接时，看起来一切正常，实际画面是黑的。
    /// </summary>
    private async Task<Result> SwitchToChildAsync(
        CancellationToken cancellationToken,
        bool existingSessionOnly)
    {
        var existing = ChildSessionNativeMethods.TryGetChildSessionId();
        if (existingSessionOnly && existing is null)
        {
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        }

        // 手动切换保留完整启动与失败兜底；纠正不进入任何可能创建会话的路径。
        if (!existingSessionOnly
            && (existing is not { } id || !ChildSessionNativeMethods.IsSessionActive(id)))
        {
            if (!await _childSessionService.StartAsync(cancellationToken).ConfigureAwait(false))
            {
                return new Result(false, Locale.Get("Uu.Result.NotConnected"));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var connected = await _childSessionService.EnsureDesktopConnectedAsync(
            cancellationToken, existingSessionOnly: existingSessionOnly)
            .ConfigureAwait(false);

        if (!connected && !existingSessionOnly)
        {
            AppLog.Warn("切回分身：直接重连未成功，改走完整启动流程。");
            if (!await _childSessionService.StartAsync(cancellationToken).ConfigureAwait(false))
            {
                return new Result(false, Locale.Get("Uu.Result.NotConnected"));
            }
            connected = await _childSessionService.EnsureDesktopConnectedAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!connected)
        {
            return new Result(false, Locale.Get("Uu.Result.NotConnected"));
        }

        var childSession = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSession is not { } sessionId)
        {
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var killed = KillStreamers(out var killFailed);
        if (killed > 0)
        {
            _killedStreamersInLastSwitch = true;
        }

        if (killFailed)
        {
            return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed"));
        }

        if (killed == 0)
        {
            return new Result(true, Locale.Get("Uu.Result.ToChild.NoStreamer"));
        }

        var settled = await WaitUntilAsync(
            () => GetStreamerSessionIds().Contains(sessionId),
            StreamerSettleWait,
            cancellationToken).ConfigureAwait(false);
        return settled
            ? new Result(true, Locale.Get("Uu.Result.ToChild"))
            : new Result(false, Locale.Get("Uu.Result.ToChild.StreamerStuck"));
    }

    /// <summary>
    /// 查 UU 串流端所在的会话号。没有串流端时返回空集合。
    /// </summary>
    internal static List<uint> GetStreamerSessionIds()
    {
        var sessions = new List<uint>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(StreamerProcessName);
        }
        catch (InvalidOperationException)
        {
            return sessions;
        }

        foreach (var process in processes)
        {
            try
            {
                sessions.Add((uint)process.SessionId);
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or NotSupportedException)
            {
                // 进程刚好退出，或权限不足读不到会话号：跳过即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        return sessions;
    }

    /// <summary>
    /// 结束全部 UU 串流端，返回结束掉的个数。
    ///
    /// UU 的服务会自动把它重新拉起来，重拉时就会重新挑会话——这正是切换的杠杆。
    /// </summary>
    /// <param name="failed">有进程存在但一个都没杀掉时为 true。</param>
    private static int KillStreamers(out bool failed)
    {
        failed = false;
        _ = ProcessNativeMethods.TryEnableDebugPrivilege();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(StreamerProcessName);
        }
        catch (InvalidOperationException)
        {
            failed = true;
            return 0;
        }

        var killed = 0;
        foreach (var process in processes)
        {
            var processId = process.Id;
            try
            {
                if (ProcessNativeMethods.TryTerminateProcess(processId, out var errorCode))
                {
                    killed++;
                }
                else
                {
                    // 原生调用失败时退回托管实现，覆盖「句柄拿不到但进程能杀」的情况。
                    process.Kill();
                    killed++;
                    AppLog.Warn($"结束串流端 {processId} 走托管路径（Win32 错误 {errorCode}）。");
                }
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or NotSupportedException)
            {
                failed = true;
                AppLog.Warn($"无法结束串流端进程 {processId}。", exception);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killed < processes.Length)
        {
            failed = true;
        }

        if (killed > 0)
        {
            AppLog.Info($"已结束 {killed} 个 UU 串流端进程，等待服务重新挑选会话。");
        }

        return killed;
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or Win32Exception)
            {
                // 查询期间会话状态在变，忽略单次失败，下一轮继续。
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UuViewSwitchService));
        }
    }
}
