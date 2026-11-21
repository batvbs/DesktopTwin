using System.Threading.Channels;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class ServiceRecoveryTests
{
    [Fact]
    public async Task StopPendingOutlivesCallerTimeoutAndStillStartsWhenItFinallyStops()
    {
        var service = new ControlledService();
        var recovery = service.CreateRecovery();
        var first = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        Assert.Equal(new[] { "stop" }, service.Commands);
        Assert.False(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(first.IsCompleted);

        // 之后的恢复请求加入同一个操作，不能在超时后再次发 stop。
        var joining = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        service.Advance(1);
        await service.WaitForPoll();
        Assert.Equal(new[] { "stop", "start" }, service.Commands);
        Assert.False(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await joining);
        Assert.True(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterStopCancelsWaitingButNotTheRecoveryObligation()
    {
        var service = new ControlledService();
        var recovery = service.CreateRecovery();
        using var cancellation = new CancellationTokenSource();
        var waiting = recovery.EnsureRunningAsync(restart: true, cancellation.Token);
        // 未取消的等待者：共享恢复即使第一个等待者取消，也要真正跑到 Running 才算空闲。
        var survivor = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));

        service.Advance(1);
        await service.WaitForPoll();
        service.Advance(4);
        Assert.Equal(
            ServiceRecovery.Outcome.Succeeded,
            await survivor.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(await recovery.WaitForIdleAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(new[] { "stop", "start" }, service.Commands);
    }

    [Fact]
    public async Task InitialStopPendingIsFollowedThroughWithoutIssuingAnotherStop()
    {
        var service = new ControlledService { State = 3 };
        var recovery = service.CreateRecovery();
        var waiting = recovery.EnsureRunningAsync(restart: false, CancellationToken.None);
        await service.WaitForPoll();
        service.Advance(1);
        await service.WaitForPoll();
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "start" }, service.Commands);
    }

    [Fact]
    public async Task CancellationDuringStartPendingDoesNotMakeTheServiceAppearIdle()
    {
        var service = new ControlledService { State = 1 };
        var recovery = service.CreateRecovery();
        using var cancellation = new CancellationTokenSource();
        var waiting = recovery.EnsureRunningAsync(restart: false, cancellation.Token);
        await service.WaitForPoll();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(recovery.IsBusy);
        Assert.False(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));
        service.Advance(4);
        Assert.True(await recovery.WaitForIdleAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(recovery.IsBusy);
        Assert.Equal(new[] { "start" }, service.Commands);
    }

    [Fact]
    public async Task FailedStopWhileStillRunningDoesNotIssueStartOrReportSuccess()
    {
        var commands = new List<string>();
        var recovery = new ServiceRecovery(() => 4,
            () => { commands.Add("stop"); return false; },
            () => { commands.Add("start"); return true; });
        Assert.Equal(ServiceRecovery.Outcome.StopFailed,
            await recovery.EnsureRunningAsync(restart: true, CancellationToken.None));
        Assert.Equal(new[] { "stop" }, commands);
    }

    [Fact]
    public async Task StartFailureRetriesTheCommandInsteadOfOnlyPolling()
    {
        var state = 1;
        var starts = 0;
        var recovery = new ServiceRecovery(() => state, () => throw new Exception("Already stopped"),
            () => { starts++; if (starts == 2) state = 4; return starts == 2; },
            _ => Task.CompletedTask);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded,
            await recovery.EnsureRunningAsync(restart: false, CancellationToken.None));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task UnknownStateDuringIssuedStopRetainsRecoveryOwnership()
    {
        var service = new ControlledService();
        var recovery = service.CreateRecovery();
        var work = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        service.Advance(null);
        await service.WaitForPoll();
        Assert.False(work.IsCompleted);
        service.Advance(1);
        await service.WaitForPoll();
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await work.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelledBeforeAdmissionDoesNotTouchTheService()
    {
        var touched = false;
        var recovery = new ServiceRecovery(() => { touched = true; return 4; }, () => true, () => true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recovery.EnsureRunningAsync(restart: true, new CancellationToken(true)));
        Assert.False(touched);
    }

    [Fact]
    public async Task AcceptedStopStillOwnsRecoveryWhenTheFirstStatusReadIsRunning()
    {
        var service = new ControlledService();
        var stopped = false;
        var recovery = new ServiceRecovery(() => service.State,
            () => { stopped = true; return true; },
            () => { service.Commands.Add("start"); service.State = 4; return true; },
            service.Delay);
        var waiting = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        Assert.True(stopped);
        Assert.False(waiting.IsCompleted);
        service.Advance(1);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "start" }, service.Commands);
    }

    [Fact]
    public async Task RejectedStopWithUnknownStatusReleasesWaitersAndAllowsAnotherRecovery()
    {
        var service = new ControlledService();
        var stops = 0;
        var recovery = new ServiceRecovery(() => service.State,
            () =>
            {
                service.Commands.Add("stop");
                service.State = ++stops == 1 ? null : 3;
                return stops > 1;
            },
            () => { service.Commands.Add("start"); service.State = 2; return true; }, service.Delay);
        var first = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        var joining = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.StopFailed, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ServiceRecovery.Outcome.StopFailed, await joining);
        Assert.False(recovery.IsBusy);
        Assert.True(await recovery.WaitForIdleAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(new[] { "stop" }, service.Commands);

        var retry = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        service.Advance(1);
        await service.WaitForPoll();
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await retry.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "stop", "stop", "start" }, service.Commands);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RejectedStopWithUnknownStatusStillRestoresAStoppedService(int observedState)
    {
        var service = new ControlledService();
        var recovery = new ServiceRecovery(() => service.State,
            () => { service.Commands.Add("stop"); service.State = null; return false; },
            () => { service.Commands.Add("start"); service.State = 2; return true; }, service.Delay);
        var waiting = recovery.EnsureRunningAsync(restart: true, CancellationToken.None);
        await service.WaitForPoll();
        service.Advance(observedState);
        if (observedState == 3)
        {
            await service.WaitForPoll();
            Assert.True(recovery.IsBusy);
            service.Advance(1);
        }

        await service.WaitForPoll();
        service.Advance(4);
        Assert.Equal(ServiceRecovery.Outcome.Succeeded, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "stop", "start" }, service.Commands);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotLeaveRejectedStopBusyWhenStatusReturnsToRunning()
    {
        var service = new ControlledService();
        var recovery = new ServiceRecovery(() => service.State,
            () => { service.State = null; return false; },
            () => throw new InvalidOperationException("No stop was accepted"), service.Delay);
        using var cancellation = new CancellationTokenSource();
        var waiting = recovery.EnsureRunningAsync(restart: true, cancellation.Token);
        await service.WaitForPoll();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(recovery.IsBusy);
        service.Advance(4);
        Assert.True(await recovery.WaitForIdleAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(recovery.IsBusy);
    }

    private sealed class ControlledService
    {
        internal int? State = 4;
        internal List<string> Commands { get; } = [];
        private readonly Channel<TaskCompletionSource> _polls = Channel.CreateUnbounded<TaskCompletionSource>();
        private TaskCompletionSource? _waiting;

        internal ServiceRecovery CreateRecovery() => new(() => State,
            () => { Commands.Add("stop"); State = 3; return true; },
            () => { Commands.Add("start"); State = 2; return true; },
            Delay);

        internal Task Delay(TimeSpan duration)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _polls.Writer.TryWrite(completion);
            return completion.Task;
        }

        internal async Task WaitForPoll() =>
            _waiting = await _polls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        internal void Advance(int? state)
        {
            State = state;
            _waiting!.SetResult();
            _waiting = null;
        }
    }
}
