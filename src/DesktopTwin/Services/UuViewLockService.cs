using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopTwin.Interop;

namespace DesktopTwin.Services;

/// <summary>
/// UU 视角锁定：定时检查 UU 串流端是否还在记录的会话里，跑掉了就切回来。
///
/// 记录时机是「打开软件」和「打开这个开关」两处；之后只有用户主动切换视角才会更新记录值，
/// 其余变化（启动分身、会话被系统换掉等）都当作异常，尽量自动纠正。
///
/// 与「UU 视角切换」相互独立：只开锁定时不注册热键、不安装分身内代理，记录值固定成
/// 电脑主机——那种用法只在主机上操作，不需要在分身里接热键。
/// </summary>
internal sealed class UuViewLockService : IDisposable
{
    /// <summary>
    /// 轮询间隔。切换本身要十几秒（断开、重连、等串流端落点），查得太勤只会
    /// 在切换过程中反复看到「位置不对」；15 秒足够快，也不会和切换抢时序。
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly UuViewSwitchService _switchService;
    private readonly DispatcherTimer _timer;

    private uint? _lockedSession;
    private bool _enabled;
    private bool _switchMode;
    private CancellationTokenSource? _switchCts;

    /// <summary>配置代际。每次启停或模式变化递增，用于丢弃旧配置下算出的纠正结果。</summary>
    private int _configGeneration;

    /// <summary>
    /// 记录的目标是不是「电脑主机」。
    ///
    /// 两种模式都需要它：只开锁定时恒为主机；两者都开时，如果记录时串流端就在主机上，
    /// 也要按主机处理。否则 Console 会话号一变（重新登录会把 1 换掉），
    /// 旧号会被当成「分身目标」，程序会把视角切到分身去。
    /// </summary>
    private bool _lockedTargetIsHost;
    private bool _switching;
    private bool _disposed;

    internal UuViewLockService(UuViewSwitchService switchService, Dispatcher dispatcher)
    {
        _switchService = switchService;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = CheckInterval
        };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// 开启或关闭锁定。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="switchEnabled">
    /// 是否同时开着「UU 视角切换」。为 false 时记录值固定为电脑主机。
    /// </param>
    internal void SetEnabled(bool enabled, bool switchEnabled)
    {
        ThrowIfDisposed();

        // 同样的启用状态和模式：保留已记录的会话。
        //
        // 这个方法会被「改热键」「切换视角开关」等路径重复调用。若每次都重新记录，
        // 恰好此时串流端已经漂到别处，就会把漂移后的位置当成新基线，等于放过一次异常。
        if (enabled && _enabled && switchEnabled == _switchMode)
        {
            return;
        }

        _timer.Stop();
        _enabled = enabled;
        _switchMode = switchEnabled;
        _configGeneration++;

        // 配置一变就掐掉在途的纠正：它可能正卡在「等分身断开」的几十秒里，
        // 按旧配置继续跑会背着用户完成一次切换。
        CancelActiveSwitch();

        if (!enabled)
        {
            _lockedSession = null;
            _lockedTargetIsHost = false;
            return;
        }

        _lockedTargetIsHost = !switchEnabled;

        _lockedSession = ResolveInitialTarget(switchEnabled);
        _lockedTargetIsHost = _lockedSession is not { } recorded
                              || recorded == ChildSessionNativeMethods.TryGetConsoleSessionId();

        AppLog.Info(_lockedTargetIsHost
            ? "UU 视角锁定：目标为电脑主机。"
            : $"UU 视角锁定：已记录会话位置 {_lockedSession?.ToString() ?? "(未知)"}。");
        _timer.Start();
    }

    /// <summary>
    /// 用户主动切换视角后，把记录值更新为新落点。
    ///
    /// 切换成功时串流端可能还在 settle（要等 UU 重新挑会话），这时去读进程会拿到旧会话；
    /// 所以由切换服务把目标会话传进来，而不是自己再查一遍。
    /// </summary>
    internal void RebaseTo(uint? session)
    {
        if (!_enabled || _disposed || session is not { } value)
        {
            return;
        }

        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        var isHost = console is null || console.Value == value;

        if (!_switchMode && !isHost)
        {
            // 只开锁定时契约是「锁在电脑主机」：手动切到分身不该改变记录值，
            // 否则锁定会跟着留在分身，和这个模式的定义相悖。
            return;
        }

        _lockedSession = value;
        _lockedTargetIsHost = isHost;
        AppLog.Info(
            isHost
                ? "UU 视角锁定：记录值更新为电脑主机。"
                : $"UU 视角锁定：记录值更新为 {value}。");
    }

    /// <summary>
    /// 记录值的初始来源：只开锁定时就是电脑主机；两个开关都开时取当前串流端所在会话
    /// （串流端还没连上则退回主机）。
    /// </summary>
    private static uint? ResolveInitialTarget(bool switchEnabled)
    {
        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        if (!switchEnabled)
        {
            return console;
        }

        var streamers = UuViewSwitchService.GetStreamerSessionIds();
        return streamers.Count > 0 ? streamers[0] : console;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (!_enabled || _switching || _disposed)
        {
            return;
        }

        // 目标是主机时每次现取 Console：它可能因为重新登录换了号。
        var target = _lockedTargetIsHost
            ? ChildSessionNativeMethods.TryGetConsoleSessionId()
            : _lockedSession;

        if (target is not { } targetSession)
        {
            return;
        }

        var streamers = UuViewSwitchService.GetStreamerSessionIds();

        // 串流端不在说明 UU 没连（或正在重连），这时无从判断位置，什么都不做。
        if (streamers.Count == 0 || streamers.Contains(targetSession))
        {
            return;
        }

        _switching = true;
        var configGeneration = _configGeneration;
        var cts = new CancellationTokenSource();
        _switchCts = cts;
        try
        {
            AppLog.Info(
                $"UU 视角锁定：串流端在 [{string.Join(",", streamers)}]，应为 {targetSession}，自动切回。");

            var result = await _switchService
                .EnsureSessionAsync(targetSession, cts.Token)
                .ConfigureAwait(true);

            if (result.Success)
            {
                // 期间用户改过配置（关掉锁定、或切换了模式）就丢掉这次结果：
                // 新配置可能有自己的基线，不能被旧纠正的落点覆盖回去。
                if (configGeneration != _configGeneration)
                {
                    return;
                }

                // 纠正时可能换了目标（记录的分身会话已失效，切到了当前那个）。
                // 基线必须跟着改，否则下一轮仍拿旧号比对，会把已经正确的位置再纠正一遍。
                if (!_lockedTargetIsHost
                    && _switchService.LastSwitchTarget is { } actual
                    && actual != targetSession)
                {
                    _lockedSession = actual;
                    AppLog.Info($"UU 视角锁定：记录值同步为 {actual}。");
                }
            }
            else
            {
                AppLog.Warn($"UU 视角锁定：自动切回未成功（{result.Message}），下一轮再试。");
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭功能或退出程序时取消，正常路径。
        }
        catch (Exception exception)
        {
            AppLog.Warn("UU 视角锁定：自动切回出错。", exception);
        }
        finally
        {
            if (ReferenceEquals(_switchCts, cts))
            {
                _switchCts = null;
            }

            cts.Dispose();
            _switching = false;
        }
    }

    private void CancelActiveSwitch()
    {
        try
        {
            _switchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收尾，忽略。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        CancelActiveSwitch();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
