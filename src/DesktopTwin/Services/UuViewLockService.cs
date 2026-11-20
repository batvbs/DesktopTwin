using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopTwin.Services;

/// <summary>
/// 每 15 秒纠正视角漂移。记录逻辑目标（主机 / 分身），会话号由切换服务现查。
/// 所有状态由 UI Dispatcher 管理；在途纠正退出前不启动下一轮。
/// </summary>
internal sealed class UuViewLockService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<UuViewTarget> _captureTarget;
    private readonly Func<UuViewTarget, bool> _needsCorrection;
    private readonly Func<UuViewTarget, CancellationToken, Task<UuViewSwitchService.Result>> _ensureTarget;
    private UuViewTarget? _target;
    private bool _switchMode;
    private CancellationTokenSource? _switchCts;
    private bool _disposed;

    internal UuViewLockService(UuViewSwitchService switchService, Dispatcher dispatcher)
        : this(dispatcher, UuViewSwitchService.CaptureTarget,
            UuViewSwitchService.NeedsCorrection, switchService.EnsureTargetAsync)
    {
    }

    internal UuViewLockService(
        Dispatcher dispatcher,
        Func<UuViewTarget> captureTarget,
        Func<UuViewTarget, bool> needsCorrection,
        Func<UuViewTarget, CancellationToken, Task<UuViewSwitchService.Result>> ensureTarget)
    {
        _captureTarget = captureTarget;
        _needsCorrection = needsCorrection;
        _ensureTarget = ensureTarget;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _timer.Tick += OnTick;
    }

    internal void SetEnabled(bool enabled, bool switchEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 改热键会重复应用配置，不能把漂移后的位置当成新基线。
        if (enabled == _target.HasValue && switchEnabled == _switchMode)
        {
            return;
        }

        _timer.Stop();
        _switchCts?.Cancel();
        _switchMode = switchEnabled;
        _target = enabled ? (switchEnabled ? _captureTarget() : UuViewTarget.Host) : null;
        if (enabled)
        {
            AppLog.Info($"UU 视角锁定：记录目标为 {_target}。");
            _timer.Start();
        }
    }

    /// <summary>手动切换成功后更新基线；只开锁定时始终保持主机目标。</summary>
    internal void RebaseTo(UuViewTarget? target)
    {
        if (_disposed || _target is null || target is null
            || (!_switchMode && target != UuViewTarget.Host))
        {
            return;
        }

        if (_target != target)
        {
            _switchCts?.Cancel();
            _target = target;
            AppLog.Info($"UU 视角锁定：记录目标更新为 {target}。");
        }
    }

    private async void OnTick(object? sender, EventArgs e) => await CheckAsync();

    internal async Task CheckAsync()
    {
        if (_disposed || _switchCts is not null || _target is not { } target)
        {
            return;
        }

        // CTS 归本轮所有。取消只发信号，不清引用、不释放，避免新旧两轮重叠。
        using var cts = new CancellationTokenSource();
        _switchCts = cts;
        try
        {
            if (!_needsCorrection(target))
            {
                return;
            }

            AppLog.Info($"UU 视角锁定：串流端偏离 {target}，自动切回。");
            var result = await _ensureTarget(target, cts.Token).ConfigureAwait(true);
            if (!result.Success && !cts.IsCancellationRequested)
            {
                AppLog.Warn($"UU 视角锁定：自动切回未成功（{result.Message}），下一轮再试。");
            }
            // 不从纠正结果更新基线，旧配置完成后也不会覆盖新配置。
        }
        catch (OperationCanceledException)
        {
            // 配置变化或退出时取消，正常路径。
        }
        catch (Exception exception)
        {
            AppLog.Warn("UU 视角锁定：自动切回出错。", exception);
        }
        finally
        {
            _switchCts = null;
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
        _switchCts?.Cancel();
    }
}
