using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionOperationsTests
{
    [Fact]
    public async Task OverlappingRequestsSharePreflightApprovalAndConnection()
    {
        using var operations = new SessionOperations();
        using var request = operations.Begin();
        var scan = Signal();
        var runs = 0;
        var approvals = 0;
        var connections = 0;
        async Task<bool> Start(CancellationToken token)
        {
            runs++;
            await scan.Task.WaitAsync(token);
            approvals++;
            connections++;
            return true;
        }

        var first = operations.StartAsync(Start, request.Token);
        var second = operations.StartAsync(Start, request.Token);
        Assert.Equal(1, runs);
        scan.SetResult();
        Assert.All(await Task.WhenAll(first, second), Assert.True);
        Assert.Equal(1, approvals);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task LogoffCancelsTheEntireSwitchIncludingItsFallbackStartup()
    {
        using var operations = new SessionOperations();
        using var switchRequest = operations.Begin();
        var scan = Signal();
        var connections = 0;
        var fallback = 0;

        async Task<bool> Start(CancellationToken token)
        {
            // 模拟不能中断的系统扫描：扫描回来后仍须识别注销。
            await scan.Task;
            token.ThrowIfCancellationRequested();
            connections++;
            return true;
        }
        async Task Switch()
        {
            await operations.StartAsync(Start, switchRequest.Token);
            fallback++;
            await operations.StartAsync(Start, switchRequest.Token);
        }

        var switching = Switch();
        operations.CancelPending();
        scan.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching);
        Assert.Equal(0, connections);
        Assert.Equal(0, fallback);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.StartAsync(Start, switchRequest.Token));
    }

    [Fact]
    public async Task NewExplicitStartDoesNotJoinOrGetClearedByAnOldCancelledStart()
    {
        using var operations = new SessionOperations();
        using var oldRequest = operations.Begin();
        var oldScan = Signal();
        var old = operations.StartAsync(async _ => { await oldScan.Task; return true; }, oldRequest.Token);
        operations.CancelPending();
        using var newRequest = operations.Begin();
        var newScan = Signal();
        var newEntered = Signal();
        var newRuns = 0;
        async Task<bool> NewStart(CancellationToken token)
        {
            newRuns++;
            newEntered.TrySetResult();
            await newScan.Task.WaitAsync(token);
            return true;
        }
        var current = operations.StartAsync(NewStart, newRequest.Token);
        oldScan.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joining = operations.StartAsync(NewStart, newRequest.Token);
        Assert.Equal(1, newRuns);
        newScan.SetResult();
        Assert.True(await current);
        Assert.True(await joining);
    }

    [Fact]
    public async Task LogoffWhileApprovalIsOpenPreventsConnectionAndQueuedRequests()
    {
        using var operations = new SessionOperations();
        using var request = operations.Begin();
        var approval = Signal();
        var connected = false;
        var start = operations.StartAsync(async token =>
        {
            await approval.Task;
            token.ThrowIfCancellationRequested();
            connected = true;
            return true;
        }, request.Token);

        operations.CancelPending();
        var queuedRan = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.StartAsync(_ =>
        {
            queuedRan = true;
            return Task.FromResult(true);
        }, request.Token));
        approval.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(connected);
        Assert.False(queuedRan);
    }

    [Fact]
    public async Task CancellingAJoiningWaiterDoesNotCancelTheSharedStart()
    {
        using var operations = new SessionOperations();
        using var request = operations.Begin();
        using var joiningCancellation = new CancellationTokenSource();
        var scan = Signal();
        var first = operations.StartAsync(async token => { await scan.Task.WaitAsync(token); return true; }, request.Token);
        var joining = operations.StartAsync(_ => throw new Exception("Must share startup"), joiningCancellation.Token);
        joiningCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joining);
        scan.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task DisposalCancelsOutstandingWorkAndRejectsNewOperations()
    {
        var operations = new SessionOperations();
        using var request = operations.Begin();
        var work = operations.StartAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, request.Token);
        operations.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Throws<ObjectDisposedException>(() => operations.Begin());
    }

    [Fact]
    public async Task ExplicitStartDuringLogoffWaitsForSessionTerminationToFinish()
    {
        using var operations = new SessionOperations();
        using var oldRequest = operations.Begin();
        var oldStarted = Signal();
        var old = operations.StartAsync(async token =>
        {
            oldStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, oldRequest.Token);
        await oldStarted.Task;
        var terminating = Signal();
        var allowTermination = Signal();
        var events = new List<string>();
        var close = operations.EndSessionAsync(async () =>
        {
            events.Add("logoff started");
            terminating.SetResult();
            await allowTermination.Task;
            events.Add("logoff completed");
        });
        await terminating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var newRequest = operations.Begin();
        var start = operations.StartAsync(_ =>
        {
            events.Add("new connection");
            return Task.FromResult(true);
        }, newRequest.Token);
        Assert.False(start.IsCompleted);
        allowTermination.SetResult();
        await close;
        Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(new[] { "logoff started", "logoff completed", "new connection" }, events);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
