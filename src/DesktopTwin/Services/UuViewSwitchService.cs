using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

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
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StreamerSettleWait = TimeSpan.FromSeconds(30);

    private readonly ChildSessionService _childSessionService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _streamRecoveryCancellation;
    private bool _killedStreamersInLastSwitch;
    private bool _disposed;

    /// <summary>
    /// 创建切换服务。
    /// </summary>
    /// <param name="childSessionService">用于断开/重连分身。</param>
    public UuViewSwitchService(ChildSessionService childSessionService)
    {
        _childSessionService = childSessionService;
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
    public readonly record struct Result(bool Success, string Message);

    /// <summary>
    /// 在「整台电脑」与「桌面分身」之间切换一次。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<Result> ToggleAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // 上一次切换还没结束就忽略本次：连点热键会让会话状态反复横跳。
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new Result(false, Locale.Get("Uu.Result.Busy"));
        }

        try
        {
            // 新的切换开始：停掉上一轮的后台恢复，避免两个流程同时动 UU 服务。
            StopStreamerRecovery();
            _killedStreamersInLastSwitch = false;

            var consoleSession = ChildSessionNativeMethods.TryGetConsoleSessionId();
            var streamerSessions = GetStreamerSessionIds();
            var childSession = ChildSessionNativeMethods.TryGetChildSessionId();

            AppLog.Info(
                $"UU 视角切换：串流端会话=[{string.Join(',', streamerSessions)}] "
                + $"Console={consoleSession?.ToString() ?? "-"} "
                + $"分身={childSession?.ToString() ?? "-"}");

            if (consoleSession is { } console && streamerSessions.Contains(console))
            {
                return await SwitchToChildAsync(cancellationToken).ConfigureAwait(false);
            }

            if (streamerSessions.Count > 0)
            {
                return await SwitchToMainAsync(cancellationToken).ConfigureAwait(false);
            }

            if (childSession is { } active
                && ChildSessionNativeMethods.IsSessionActive(active))
            {
                return await SwitchToChildAsync(cancellationToken).ConfigureAwait(false);
            }

            return await SwitchToMainAsync(cancellationToken).ConfigureAwait(false);
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
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 切换进行中窗口被关掉，服务已释放；此时放行信号量已无意义。
            }

            // 这一次切换里如果真的结束过串流端，而它迟迟没回来，就交给后台持续重试：
            // UU 服务在短时间内被连续结束时会进入重启退避，之后不再拉起串流端，
            // 手机上看到的就是「设备离线」，等多久也不会自愈。
            ScheduleStreamerRecovery();
        }
    }

    /// <summary>
    /// 串流端是否正在被自动恢复（供界面显示状态）。
    /// </summary>
    public bool IsStreamerRecoveryRunning => _streamRecoveryCancellation is not null;

    /// <summary>
    /// 启动（或重启）串流端的后台恢复循环。
    ///
    /// 只在「这次切换确实结束过串流端」时才启动：手机没连着的时候本来就没有串流端，
    /// 那种情况下重启 UU 服务纯属添乱。
    /// </summary>
    private void ScheduleStreamerRecovery()
    {
        if (_disposed)
        {
            return;
        }

        StopStreamerRecovery();

        if (!_killedStreamersInLastSwitch || GetStreamerSessionIds().Count > 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _streamRecoveryCancellation = cancellation;
        _ = Task.Run(() => RecoverStreamerAsync(cancellation.Token));
    }

    /// <summary>
    /// 停止后台恢复循环（切换开始时、退出时调用）。
    /// </summary>
    public void StopStreamerRecovery()
    {
        var cancellation = _streamRecoveryCancellation;
        _streamRecoveryCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

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
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds + 10));
                attempt++;

                if (GetStreamerSessionIds().Count > 0)
                {
                    AppLog.Info($"UU 串流端已恢复（自动恢复第 {attempt} 次检查时发现）。");
                    _streamRecoveryCancellation = null;
                    RaiseStreamerRecovered();
                    return;
                }

                AppLog.Warn($"UU 串流端仍未拉起，第 {attempt} 次尝试恢复 UU 服务。");

                if (attempt % 4 == 0)
                {
                    // 服务本身在跑但不再拉起串流端，说明它退避了：完整重启一次。
                    await RunServiceCommandAsync("stop", cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    await RunServiceCommandAsync("start", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await RunServiceCommandAsync("start", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 切换开始或程序退出：正常结束。
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("UU 串流自动恢复循环异常结束。", exception);
        }
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
        _gate.Dispose();
    }

    /// <summary>
    /// 重启 UU 的串流服务，用于「切换后手机显示设备离线」时的人工恢复。
    ///
    /// 背景：切换的杠杆就是结束 GameViewerServer，让服务重新挑会话。短时间内连续切换
    /// （实测两分钟内四次）会让服务把它当成「连续崩溃」而进入重启退避，之后就不再拉起
    /// 串流端——手机上看到的就是「设备离线」。重启服务可以清掉这个状态。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<Result> RestartStreamerServiceAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        try
        {
            AppLog.Info("正在重启 UU 服务 GameViewerService…");

            var stop = await RunServiceCommandAsync("stop", cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var start = await RunServiceCommandAsync("start", cancellationToken).ConfigureAwait(false);

            if (!start)
            {
                return new Result(false, $"重启 UU 服务失败（停止{(stop ? "成功" : "失败")}、启动失败）。请在服务管理器里手动重启 GameViewerService。");
            }

            // 服务起来后，串流端要等客户端连入才会拉起；这里只确认服务状态。
            var settled = await WaitUntilAsync(
                () => GetStreamerSessionIds().Count > 0,
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            return new Result(
                true,
                settled
                    ? Locale.Get("Uu.Result.ServiceRestarted")
                    : Locale.Get("Uu.Result.ServiceRestarted.NoStreamer"));
        }
        catch (OperationCanceledException)
        {
            return new Result(false, Locale.Get("Uu.Result.Cancelled"));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("重启 UU 服务失败。", exception);
            return new Result(false, Locale.Format("Uu.Result.Failed", exception.GetBaseException().Message));
        }
    }

    private static async Task<bool> RunServiceCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "sc.exe"),
                $"\"{command}\" \"{StreamerServiceName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // sc.exe 的退出码不可全信（「服务已在运行」等也算失败位），
            // 真正的判据是服务状态，交给调用方看。
            AppLog.Info($"sc {command} {StreamerServiceName} 退出码 {process.ExitCode}。");
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            AppLog.Warn($"执行 sc {command} 失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 切到「整台电脑」：断开分身，逼 UU 落到 Console。
    /// </summary>
    private async Task<Result> SwitchToMainAsync(CancellationToken cancellationToken)
    {
        var childSession = ChildSessionNativeMethods.TryGetChildSessionId();

        // 先让 RDP 客户端自己断开：这条路径是「有意的断开」，
        // 不会触发「RDP 连接意外断开」的失败提示。
        _ = _childSessionService.DisconnectDesktopForSwitch();

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

        var killed = KillStreamers();
        // 记下来：如果它之后没回来，后台恢复循环据此决定是否重试拉起。
        _killedStreamersInLastSwitch = killed > 0;
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
    private async Task<Result> SwitchToChildAsync(CancellationToken cancellationToken)
    {
        // 1) 先看分身到底在不在：可能被关掉、断开、甚至整个注销了。
        var existing = ChildSessionNativeMethods.TryGetChildSessionId();
        var sessionAlive = existing is { } existingId
            && ChildSessionNativeMethods.IsSessionActive(existingId);

        if (!sessionAlive)
        {
            AppLog.Info(existing is null
                ? "切回分身：会话不存在（被关闭或注销），走完整启动流程。"
                : $"切回分身：会话 {existing} 不是 Active，重新连接。");
            await _childSessionService.StartAsync().ConfigureAwait(true);
        }

        // 2) 确保 RDP 连上。
        //    先清掉「正在断开」的排队状态：Disconnect() 之后要等 OnDisconnected 复位，
        //    回调丢了就会一直卡住，重连只排队不执行——用户看到的就是画面一直黑着。
        _childSessionService.ResetDisconnectState();

        var connected = await _childSessionService
            .EnsureDesktopConnectedAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!connected)
        {
            // 重连没成功，最常见的原因是断开事件没回来：RdpActiveXHost 里
            // Disconnect() 会置 _disconnectRequested，要等 OnDisconnected 回调复位
            // 才会真正发起下一次连接。回调丢了就卡在「正在断开」的状态里。
            // 这里改走一次完整启动流程兜底，而不是让用户自己去点「启动并连接」。
            AppLog.Warn("切回分身：直接重连未成功，改走完整启动流程。");
            await _childSessionService.StartAsync().ConfigureAwait(true);
            connected = await _childSessionService
                .EnsureDesktopConnectedAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (!connected)
        {
            return new Result(false, Locale.Get("Uu.Result.NotConnected"));
        }

        // 3) 会话号重新取一次：上面可能刚重建过，旧的号已经失效。
        var childSession = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSession is not { } sessionId)
        {
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        }

        // 4) 弹掉串流端，让服务落到分身。这一步不能因为「已连接」而跳过：
        //    连接只代表本机能看到画面，UU 那边还挂在 Console 上。
        var killed = KillStreamers();
        // 记下来：如果它之后没回来，后台恢复循环据此决定是否重试拉起。
        _killedStreamersInLastSwitch = killed > 0;
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
    private static List<uint> GetStreamerSessionIds()
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
    private static int KillStreamers()
    {
        _ = ProcessNativeMethods.TryEnableDebugPrivilege();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(StreamerProcessName);
        }
        catch (InvalidOperationException)
        {
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
                AppLog.Warn($"无法结束串流端进程 {processId}。", exception);
            }
            finally
            {
                process.Dispose();
            }
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
