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

    /// <summary>正在跑的后台恢复任务，切换前要等它收手。</summary>
    private Task? _recoveryTask;

    /// <summary>
    /// 串行化所有「停服务 / 启服务」操作。
    ///
    /// 恢复循环和手动重启都会停再启同一个服务：两边叠在一起时，一边刚停、
    /// 另一边已经 start，最后停在哪一步完全看时序。加闸门后它们排队执行，
    /// 切换也不必再靠「等若干秒」来避开对方。
    /// </summary>
    private readonly SemaphoreSlim _serviceGate = new(1, 1);

    /// <summary>单条 sc 命令的硬上限：它卡住时不能让切换或恢复永久挂起。</summary>
    private static readonly TimeSpan ServiceCommandTimeout = TimeSpan.FromSeconds(30);
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
            // 新的切换开始：停掉上一轮的后台恢复，并等它收手——它可能正处在
            // 不接取消的 stop→start 收尾里，不等就会被两边同时动 UU 服务。
            //
            // 但不清 _killedStreamersInLastSwitch：上一轮如果杀掉串流端后它一直没回来，
            // 这份「待恢复」的义务要跨过本次切换保留下来，否则恢复就断在半路，
            // 而两条切换路径都会因为「没有串流端」报成功。
            await StopStreamerRecoveryAsync().ConfigureAwait(false);

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

        if (_recoveryTask is { IsCompleted: false })
        {
            // 上一轮的收尾还没结束：它仍然在动 UU 服务，这里再起一个只会互相打架。
            // 但也不能就此丢掉这次请求——等它结束后补排一次，否则串流端一直不回来，
            // 设备就停在那儿离线，直到用户再切一次。
            AppLog.Warn("上一轮 UU 恢复仍未结束，等它结束后再排一次。");

            _ = _recoveryTask.ContinueWith(
                _ => ScheduleStreamerRecovery(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return;
        }

        if (GetStreamerSessionIds().Count > 0)
        {
            // 串流端已经在：上一轮的恢复已经成功，义务解除。
            _killedStreamersInLastSwitch = false;
            return;
        }

        if (!_killedStreamersInLastSwitch)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _streamRecoveryCancellation = cancellation;
        _recoveryTask = Task.Run(() => RecoverStreamerAsync(cancellation.Token));
    }

    /// <summary>
    /// 停掉恢复循环并等它真正收手。
    ///
    /// 恢复循环里有一段 stop→start 的收尾是不接取消的（停了就必须启回来），
    /// 新一轮切换如果不等它，两边会同时动 UU 服务：切换可能看到「没有串流端」
    /// 而报成功，旧恢复循环随后又把服务启起来。
    /// </summary>
    public async Task StopStreamerRecoveryAsync()
    {
        StopStreamerRecovery();

        var task = _recoveryTask;
        if (task is null)
        {
            return;
        }

        try
        {
            // 上限不必覆盖恢复里的完整重启（那可能有几十秒）：闸门已经保证切换的
            // 重启会排在它后面，等不到也不会并发。
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 收尾还没完（最坏是 stop 后等 15 秒超时）：不再拖住切换，
            // 但它已经收到取消，收尾结束后会自行退出。
            //
            // 关键是不清 _recoveryTask：任务还活着，ScheduleStreamerRecovery 据此
            // 不再起新的恢复循环，避免两个循环同时动 UU 服务。
            AppLog.Warn("等待 UU 恢复循环退出超时，本次不再启动新的恢复。");
            return;
        }
        catch (OperationCanceledException)
        {
        }

        _recoveryTask = null;
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

                    // 义务到此为止：不清的话，下次「没杀到串流端」的切换又会触发恢复，
                    // 而恢复每四轮就重启一次服务，平白打断正在用的串流。
                    _killedStreamersInLastSwitch = false;
                    _streamRecoveryCancellation = null;
                    RaiseStreamerRecovered();
                    return;
                }

                AppLog.Warn($"UU 串流端仍未拉起，第 {attempt} 次尝试恢复 UU 服务。");

                if (attempt % 4 == 0)
                {
                    // 服务本身在跑但不再拉起串流端，说明它退避了：完整重启一次。
                    // 与手动重启共用同一个带闸门的实现，两边不会互相踩。
                    await RestartServiceCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // 这条 start 也要过闸门：否则它会插进手动重启的 stop 与 start 之间，
                    // 让那次「等它停下」永远等不到，用户看到的是「无法修复串流」。
                    await _serviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await RunServiceCommandAsync("start", cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _serviceGate.Release();
                    }
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

            if (!await RestartServiceCoreAsync(cancellationToken).ConfigureAwait(false))
            {
                return new Result(false, Locale.Format(
                    "Uu.Result.ServiceRestartFailed2",
                    Locale.Get("Common.Failed")));
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

            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeout.CancelAfter(ServiceCommandTimeout);

            try
            {
                await process.WaitForExitAsync(commandTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // sc.exe 自己卡住（服务控制管理器不响应）：杀掉它按失败处理，
                // 否则调用方会在这里无限等下去，恢复循环也永远退不出去。
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }

                AppLog.Warn($"sc {command} {StreamerServiceName} 超过 {ServiceCommandTimeout.TotalSeconds} 秒未返回。");
                return false;
            }

            // sc.exe 的退出码不可全信：「服务已在运行」「服务已停止」都落在失败位。
            // 所以不看退出码，等状态收敛到目标值——否则 start 被拒也会报成功，
            // 上层「重启 UU 服务」就变成了一句空话。
            AppLog.Info($"sc {command} {StreamerServiceName} 退出码 {process.ExitCode}。");

            var expectRunning = !string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase);
            return await WaitForServiceStateAsync(expectRunning, cancellationToken).ConfigureAwait(false);
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
    /// 停掉再启动 UU 服务，保证「停了一定会启」。
    ///
    /// 全程持有闸门：恢复循环与手动重启不会同时动同一个服务。
    /// 每一步都有界，任何一步失败都不会留下一个停着的服务。
    /// </summary>
    private async Task<bool> RestartServiceCoreAsync(CancellationToken cancellationToken)
    {
        await _serviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopped = await RunServiceCommandAsync("stop", CancellationToken.None).ConfigureAwait(false);
            if (!stopped)
            {
                // stop 没在 15 秒内到 STOPPED：再等一轮。在 STOP_PENDING 上发 start
                // 会被直接拒绝，那就成了「只试了一次、服务还停着」。
                stopped = await WaitForServiceStateAsync(expectRunning: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!stopped)
            {
                AppLog.Warn("UU 服务未能停下，跳过本次重启。");
                return false;
            }

            var started = await RunServiceCommandAsync("start", CancellationToken.None).ConfigureAwait(false);
            if (!started)
            {
                // 刚停下时启动偶尔会被拒：再等一轮状态确认。
                started = await WaitForServiceStateAsync(expectRunning: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return started;
        }
        finally
        {
            _serviceGate.Release();
        }
    }

    /// <summary>
    /// 等 UU 服务到达目标状态。
    /// </summary>
    private static async Task<bool> WaitForServiceStateAsync(
        bool expectRunning,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (ServiceNativeMethods.TryQueryState(StreamerServiceName, out var state))
            {
                // 1 = stopped，4 = running。
                if (expectRunning ? state == 4 : state == 1)
                {
                    return true;
                }
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
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
            return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed"));
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
