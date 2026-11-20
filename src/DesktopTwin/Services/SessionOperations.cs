using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 一次会话生命周期内的操作共享取消边界；注销使整条调用链失效，显式的新操作使用新边界。
/// 同时只允许一次启动，预检、确认与连接都属于这一次启动。
/// </summary>
internal sealed class SessionOperations : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private Task<bool>? _startup;
    private bool _disposed;

    internal CancellationTokenSource Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        }
    }

    internal Task<bool> StartAsync(Func<CancellationToken, Task<bool>> start, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        CancellationTokenSource operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_startup is { IsCompleted: false })
            {
                return _startup.WaitAsync(cancellationToken);
            }

            operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _startup = completion.Task;
        }

        // 在调用线程执行，保留 WPF 上下文；先发布共享任务，再执行可能同步完成的启动。
        _ = CompleteAsync(start, operation, completion);
        return completion.Task;
    }

    private async Task CompleteAsync(
        Func<CancellationToken, Task<bool>> start,
        CancellationTokenSource operation,
        TaskCompletionSource<bool> completion)
    {
        using (operation)
        {
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                await _transition.WaitAsync(operation.Token);
                try
                {
                    var connected = await start(operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    completion.TrySetResult(connected);
                }
                finally
                {
                    _transition.Release();
                }
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    internal void CancelPending()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _lifetime;
            _lifetime = new();
            _startup = null;
        }

        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>先取消旧启动，再完成注销；新启动等待注销完成，不能和 WTS 终止会话交叉。</summary>
    internal async Task EndSessionAsync(Func<Task> endSession)
    {
        CancelPending();
        await _transition.WaitAsync();
        try
        {
            await endSession();
        }
        finally
        {
            _transition.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
