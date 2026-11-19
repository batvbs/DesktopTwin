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

        _timer.Stop();
        _enabled = enabled;

        if (!enabled)
        {
            _lockedSession = null;
            return;
        }

        _lockedSession = ResolveInitialTarget(switchEnabled);
        AppLog.Info($"UU 视角锁定：已记录会话位置 {_lockedSession?.ToString() ?? "(未知)"}。");
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

        _lockedSession = value;
        AppLog.Info($"UU 视角锁定：记录值更新为 {value}。");
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
        if (!_enabled || _switching || _disposed || _lockedSession is not { } target)
        {
            return;
        }

        var streamers = UuViewSwitchService.GetStreamerSessionIds();

        // 串流端不在说明 UU 没连（或正在重连），这时无从判断位置，什么都不做。
        if (streamers.Count == 0 || streamers.Contains(target))
        {
            return;
        }

        _switching = true;
        try
        {
            AppLog.Info(
                $"UU 视角锁定：串流端在 [{string.Join(",", streamers)}]，应为 {target}，自动切回。");

            var result = await _switchService
                .EnsureSessionAsync(target, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
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
            _switching = false;
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
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
