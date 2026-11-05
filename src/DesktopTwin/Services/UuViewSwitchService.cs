using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;

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

    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StreamerSettleWait = TimeSpan.FromSeconds(30);

    private readonly ChildSessionService _childSessionService;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            return new Result(false, "上一次切换还没结束，已忽略本次。");
        }

        try
        {
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
            return new Result(false, "切换已取消。");
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("UU 视角切换失败。", exception);
            return new Result(false, $"切换失败：{exception.GetBaseException().Message}");
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
        _gate.Dispose();
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
                    return new Result(false, "分身会话没能断开，请稍后重试。");
                }
            }
        }

        var killed = KillStreamers();
        if (killed == 0)
        {
            // 手机没连着的时候本来就没有串流端，分身已经断开，目的已经达到。
            return new Result(true, "已切到「整台电脑」。当前没有 UU 串流端，下次连入即是主桌面。");
        }

        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        var settled = console is { } consoleId
            && await WaitUntilAsync(
                () => GetStreamerSessionIds().Contains(consoleId),
                StreamerSettleWait,
                cancellationToken).ConfigureAwait(false);

        return settled
            ? new Result(true, "已切到「整台电脑」，重新连一下 UU 就进主桌面。")
            : new Result(false, "UU 串流端没能落到主桌面，再按一次热键重试。");
    }

    /// <summary>
    /// 切回「桌面分身」：把分身连回来，逼 UU 落到分身。
    /// </summary>
    private async Task<Result> SwitchToChildAsync(CancellationToken cancellationToken)
    {
        var connected = await _childSessionService
            .EnsureDesktopConnectedAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!connected)
        {
            return new Result(false, "分身没能连上。请在窗口里手动点「启动并连接」。");
        }

        var childSession = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSession is not { } sessionId)
        {
            return new Result(false, "分身会话不存在，请在窗口里手动点「启动并连接」。");
        }

        var killed = KillStreamers();
        if (killed == 0)
        {
            return new Result(true, "已切回「桌面分身」。当前没有 UU 串流端，下次连入即是游戏桌面。");
        }

        var settled = await WaitUntilAsync(
            () => GetStreamerSessionIds().Contains(sessionId),
            StreamerSettleWait,
            cancellationToken).ConfigureAwait(false);

        return settled
            ? new Result(true, "已切回「桌面分身」，重新连一下 UU 就进游戏桌面。")
            : new Result(false, "UU 串流端没能落到分身，再按一次热键重试。");
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
