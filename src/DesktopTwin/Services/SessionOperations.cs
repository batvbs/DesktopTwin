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
    private TaskCompletionSource? _sessionEnd;
    private int _endingSessions;
    private bool _disposed;

    internal CancellationTokenSource Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        }
    }

    /// <summary>注销期间的新操作等待终止完成；等待也受本次操作的取消边界约束。</summary>
    internal Task WaitForSessionEndAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_sessionEnd?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
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
                await WaitForSessionEndAsync(operation.Token);
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
        CancellationTokenSource previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // 关闭准入和更新取消边界必须一起发布，不能让新的通知趁注销未完成时重连。
            _sessionEnd ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            _endingSessions++;
            previous = _lifetime;
            _lifetime = new();
            _startup = null;
        }

        try
        {
            try { previous.Cancel(); }
            finally { previous.Dispose(); }
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
        finally
        {
            lock (_gate)
            {
                // 重叠注销全部收尾后才重新开放，失败也不能留下永久关闭的准入。
                if (--_endingSessions == 0)
                {
                    _sessionEnd!.TrySetResult();
                    _sessionEnd = null;
                }
            }
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
