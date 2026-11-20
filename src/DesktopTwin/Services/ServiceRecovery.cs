using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 持有一次服务恢复，直到已发出的停止/启动请求结束。调用方取消或超时只结束等待，
/// 不撤销 SCM 已接受的命令；新的调用加入在途操作，不会另起一组 stop/start。
/// </summary>
internal sealed class ServiceRecovery(
    Func<int?> queryState,
    Func<bool> requestStop,
    Func<bool> requestStart,
    Func<TimeSpan, Task>? delay = null)
{
    internal enum Outcome { Succeeded, StopFailed, StartFailed }

    private const int Stopped = 1;
    private const int StartPending = 2;
    private const int StopPending = 3;
    private const int Running = 4;
    private readonly object _gate = new();
    private readonly Func<TimeSpan, Task> _delay = delay ?? (duration => Task.Delay(duration));
    private Task<Outcome>? _active;

    internal bool IsBusy
    {
        get { lock (_gate) return _active is { IsCompleted: false }; }
    }

    internal Task<Outcome> EnsureRunningAsync(bool restart, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_active is not { IsCompleted: false })
            {
                _active = Task.Run(() => RecoverAsync(restart, cancellationToken));
            }

            return _active.WaitAsync(cancellationToken);
        }
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<Outcome>? active;
        lock (_gate) active = _active;
        if (active is null) return true;
        try
        {
            await active.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && active.IsCanceled)
        {
            // 命令发出前取消的操作没有系统副作用。
            return true;
        }
    }

    private async Task<Outcome> RecoverAsync(bool restart, CancellationToken admissionToken)
    {
        var state = queryState();
        if (state is null) return restart ? Outcome.StopFailed : Outcome.StartFailed;
        if (state is StartPending or StopPending) state = await WaitForStableStateAsync().ConfigureAwait(false);

        // 还未发命令时可以取消；一旦发出 stop，就必须负责把服务带回运行态。
        admissionToken.ThrowIfCancellationRequested();
        if (restart && state == Running)
        {
            var accepted = requestStop();
            state = queryState();
            // 接受 stop 后，短暂读到 RUNNING 也不代表停止请求被撤销。
            // 保留责任直到确实停止，不能以一次非 pending 状态结束收尾。
            while (state != Stopped)
            {
                // stop 被拒绝后，查询失败只代表暂时未知；恢复查询后仍在运行就应退出。
                // 每次查询都重新判断，不能因为第一次读到 null 就永久等一个未接受的 stop。
                if (!accepted && state is not (null or StopPending)) return Outcome.StopFailed;
                await _delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                state = queryState();
            }
        }

        if (state == Running) return Outcome.Succeeded;
        if (state != Stopped) return Outcome.StartFailed;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var accepted = requestStart();
            state = queryState();
            if (accepted || state is null or StartPending or StopPending)
            {
                state = await WaitForStableStateAsync().ConfigureAwait(false);
            }

            if (state == Running) return Outcome.Succeeded;
            if (state != Stopped) return Outcome.StartFailed;
            if (attempt == 0) await _delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        return Outcome.StartFailed;
    }

    private async Task<int> WaitForStableStateAsync()
    {
        while (true)
        {
            var state = queryState();
            if (state is not null and not StartPending and not StopPending) return state.Value;
            await _delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }
    }
}
