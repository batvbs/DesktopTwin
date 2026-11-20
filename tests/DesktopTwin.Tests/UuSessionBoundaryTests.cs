using System.Reflection;
using System.Runtime.CompilerServices;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class UuSessionBoundaryTests
{
    [Fact]
    public async Task LockNotificationDuringLogoffWaitsForTerminationBeforeActing()
    {
        using var harness = new Harness();
        var terminate = Signal();
        var closing = harness.Operations.EndSessionAsync(async () =>
        {
            harness.Events.Add("logoff started");
            await terminate.Task;
            harness.Events.Add("logoff completed");
        });
        var checking = harness.NotifyDrift();
        Assert.False(checking.IsCompleted);
        Assert.Equal(new[] { "logoff started" }, harness.Events);
        terminate.SetResult();
        await closing;
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "logoff started", "logoff completed", "correction" }, harness.Events);
    }

    [Fact]
    public async Task AnotherLogoffCancelsAnAlreadyDeferredCorrection()
    {
        using var harness = new Harness();
        var firstTermination = Signal();
        var secondTermination = Signal();
        var first = harness.Operations.EndSessionAsync(() => firstTermination.Task);
        var checking = harness.NotifyDrift();
        Assert.False(checking.IsCompleted);
        var second = harness.Operations.EndSessionAsync(() => secondTermination.Task);
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        firstTermination.SetResult();
        await first;
        secondTermination.SetResult();
        await second;
        Assert.Empty(harness.Events);
    }

    [Fact]
    public async Task DisablingLockCancelsDeferredCorrectionAndLeavesSwitchingAvailable()
    {
        using var harness = new Harness();
        var terminate = Signal();
        var closing = harness.Operations.EndSessionAsync(() => terminate.Task);
        var checking = harness.NotifyDrift();
        Assert.False(checking.IsCompleted);
        harness.Lock.SetEnabled(false, true);
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        terminate.SetResult();
        await closing;
        Assert.Empty(harness.Events);
        var result = await harness.Execute(_ => Task.FromResult(new UuViewSwitchService.Result(true, "manual")),
            CancellationToken.None);
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectDuringLogoffCanBeCancelledBeforeAccessingTheRdpWindow(bool background)
    {
        using var harness = new Harness();
        var terminate = Signal();
        var closing = harness.Operations.EndSessionAsync(() => terminate.Task);
        using var cancellation = new CancellationTokenSource();
        var reconnecting = background
            ? harness.Child.ConnectDesktopInBackgroundAsync(cancellation.Token)
            : harness.Child.EnsureDesktopConnectedAsync(cancellation.Token, existingSessionOnly: true);
        Assert.False(reconnecting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnecting);
        terminate.SetResult();
        await closing;
        Assert.Empty(harness.Events);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness : IDisposable
    {
        private static readonly MethodInfo ExecuteMethod = typeof(UuViewSwitchService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly UuViewSwitchService _switching;
        private readonly Queue<Action> _queue = new();
        private Action _changed = () => { };
        private bool _drifted;
        internal SessionOperations Operations { get; } = new();
        internal ChildSessionService Child { get; }
        internal List<string> Events { get; } = [];
        internal UuViewLockService Lock { get; }

        internal Harness()
        {
            // 不构造真实 RDP 窗口，也不提交服务命令；仍经过生产代码的会话边界与 UU 操作入口。
            Child = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
            typeof(ChildSessionService).GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Child, Operations);
            _switching = new UuViewSwitchService(Child);
            Lock = new UuViewLockService(() => UuViewTarget.Child, _ => _drifted,
                (_, token) => Execute(_ =>
                {
                    Events.Add("correction");
                    return Task.FromResult(new UuViewSwitchService.Result(true, "done"));
                }, token),
                (changed, _) => { _changed = changed; return new Subscription(); }, _queue.Enqueue);
            Lock.SetEnabled(true, true);
            while (_queue.TryDequeue(out var initial)) initial();
        }

        internal Task NotifyDrift()
        {
            _drifted = true;
            _changed();
            return Lock.CheckAsync();
        }

        internal Task<UuViewSwitchService.Result> Execute(
            Func<CancellationToken, Task<UuViewSwitchService.Result>> action, CancellationToken token) =>
            (Task<UuViewSwitchService.Result>)ExecuteMethod.Invoke(_switching, new object[] { action, token })!;

        public void Dispose()
        {
            Lock.Dispose();
            _switching.Dispose();
            Operations.Dispose();
        }
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose() { }
    }
}
