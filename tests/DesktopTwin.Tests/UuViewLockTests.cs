using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class UuViewLockTests
{
    [Fact]
    public async Task ReapplyingConfigurationDoesNotCaptureDrift()
    {
        var harness = new Harness();
        using var service = harness.Create();
        service.SetEnabled(true, true);
        harness.Current = UuViewTarget.Host;
        service.SetEnabled(true, true);
        await service.CheckAsync();

        Assert.Equal(new[] { UuViewTarget.Child }, harness.Requests);
    }

    [Fact]
    public async Task LockOnlyIgnoresChildCaptureAndManualChildResult()
    {
        var harness = new Harness();
        using var service = harness.Create();
        service.SetEnabled(true, false);
        service.RebaseTo(UuViewTarget.Child);
        await service.CheckAsync();

        Assert.Equal(new[] { UuViewTarget.Host }, harness.Requests);
    }

    [Fact]
    public async Task ManualSwitchRebasesButDisablingSwitchingRestoresHost()
    {
        var harness = new Harness { Current = UuViewTarget.Host };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        service.RebaseTo(UuViewTarget.Child);
        await service.CheckAsync();
        service.SetEnabled(true, false);
        await service.CheckAsync();

        Assert.Equal(new[] { UuViewTarget.Child, UuViewTarget.Host }, harness.Requests);
    }

    [Fact]
    public async Task ReconfigurationCancelsAndWaitsForOldCorrectionWithoutAdoptingItsResult()
    {
        var completion = new TaskCompletionSource<UuViewSwitchService.Result>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness { Complete = () => completion.Task };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        var pending = service.CheckAsync();
        await service.CheckAsync();
        Assert.Single(harness.Requests);

        service.SetEnabled(false, true);
        Assert.True(harness.LastToken.IsCancellationRequested);
        service.SetEnabled(true, false);
        await service.CheckAsync();
        Assert.Single(harness.Requests);

        // 模拟不接受取消、迟到的旧任务；它不能改掉新配置或放行重叠纠正。
        completion.SetResult(new(true, "old result", UuViewTarget.Child));
        await pending;
        harness.Complete = () => Task.FromResult(new UuViewSwitchService.Result(true, "done"));
        await service.CheckAsync();
        Assert.Equal(new[] { UuViewTarget.Child, UuViewTarget.Host }, harness.Requests);
    }

    [Fact]
    public async Task ManualRebaseCancelsOldCorrectionAndKeepsNewTarget()
    {
        var completion = new TaskCompletionSource<UuViewSwitchService.Result>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness { Complete = () => completion.Task };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        var pending = service.CheckAsync();
        service.RebaseTo(UuViewTarget.Host);
        Assert.True(harness.LastToken.IsCancellationRequested);

        completion.SetResult(new(true, "old result", UuViewTarget.Child));
        await pending;
        await service.CheckAsync();
        Assert.Equal(new[] { UuViewTarget.Child, UuViewTarget.Host }, harness.Requests);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancelled")]
    public async Task UnsuccessfulCorrectionAllowsNextEvent(string outcome)
    {
        var harness = new Harness
        {
            Complete = () => outcome switch
            {
                "exception" => Task.FromException<UuViewSwitchService.Result>(new InvalidOperationException()),
                "cancelled" => Task.FromCanceled<UuViewSwitchService.Result>(new CancellationToken(true)),
                _ => Task.FromResult(new UuViewSwitchService.Result(false, "retry"))
            }
        };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        await service.CheckAsync();
        harness.Complete = () => Task.FromResult(new UuViewSwitchService.Result(true, "done"));
        // 纠正失败后的重试必须由下一次通知触发（走 RequestCheck 排程），
        // 直接调 CheckAsync 会绕过排程、漏掉排程本身的回归。
        harness.Changed();
        harness.Flush();

        Assert.Equal(2, harness.Requests.Count);
    }

    [Fact]
    public async Task FailedObservationDoesNotDisableFutureChecks()
    {
        var harness = new Harness();
        using var service = harness.Create();
        service.SetEnabled(true, true);
        harness.NeedsCorrection = _ => throw new InvalidOperationException();
        await service.CheckAsync();
        Assert.Empty(harness.Requests);
        harness.NeedsCorrection = _ => true;
        await service.CheckAsync();
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task NoDriftAndDisabledLockDoNotSwitch()
    {
        var harness = new Harness { NeedsCorrection = _ => false };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        await service.CheckAsync();
        service.SetEnabled(false, true);
        harness.NeedsCorrection = _ => true;
        await service.CheckAsync();
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task DisposeCancelsPendingCorrectionAndRejectsFurtherWork()
    {
        var harness = new Harness();
        harness.Complete = async () =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, harness.LastToken);
            return new(true, "unreachable");
        };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        var pending = service.CheckAsync();
        service.Dispose();
        Assert.True(harness.LastToken.IsCancellationRequested);
        await pending;
        await service.CheckAsync();
        Assert.Single(harness.Requests);
        Assert.Throws<ObjectDisposedException>(() => service.SetEnabled(true, true));
    }

    [Fact]
    public void IdleLockOnlyChecksOnEnableAndAfterNotifications()
    {
        var observations = 0;
        var harness = new Harness { NeedsCorrection = _ => { observations++; return false; } };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        harness.Flush();
        Assert.Equal(1, observations);
        Assert.Empty(harness.Queue);
        harness.Flush();
        Assert.Equal(1, observations);

        for (var i = 0; i < 100; i++) harness.Changed();
        Assert.Single(harness.Queue);
        harness.Flush();
        Assert.Equal(2, observations);
        Assert.Empty(harness.Requests);
        Assert.Empty(harness.Queue);
    }

    [Fact]
    public async Task NotificationsDuringCorrectionCoalesceIntoOneFollowUp()
    {
        var completion = new TaskCompletionSource<UuViewSwitchService.Result>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness { NeedsCorrection = _ => false };
        using var service = harness.Create();
        service.SetEnabled(true, true);
        harness.Flush();
        harness.NeedsCorrection = _ => true;
        harness.Complete = () => completion.Task;
        var pending = service.CheckAsync();

        for (var i = 0; i < 100; i++) harness.Changed();
        Assert.Empty(harness.Queue);
        Assert.Single(harness.Requests);
        completion.SetResult(new(true, "done"));
        await pending;
        Assert.Single(harness.Queue);
        harness.Flush();
        Assert.Equal(2, harness.Requests.Count);
        Assert.Empty(harness.Queue);
    }

    [Fact]
    public void DisableDisposesMonitorAndDiscardsQueuedNotifications()
    {
        var harness = new Harness();
        using var service = harness.Create();
        service.SetEnabled(true, true);
        service.SetEnabled(true, true);
        Assert.Equal(1, harness.Subscriptions);
        service.SetEnabled(false, true);
        harness.Changed();
        harness.Flush();
        Assert.Equal(1, harness.Disposals);
        Assert.Empty(harness.Requests);

        service.SetEnabled(true, false);
        harness.Flush();
        Assert.Equal(2, harness.Subscriptions);
        Assert.Equal(new[] { UuViewTarget.Host }, harness.Requests);
    }

    [Fact]
    public void SubscriptionFailureDoesNotLeaveLockEnabledAndCanRetry()
    {
        var harness = new Harness { FailSubscription = true };
        using var service = harness.Create();
        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true, true));
        service.RequestCheck();
        Assert.Empty(harness.Queue);
        harness.FailSubscription = false;
        service.SetEnabled(true, true);
        harness.Flush();
        Assert.Single(harness.Requests);
    }

    [Fact]
    public void LostSubscriptionDisablesLockAndReportsFailure()
    {
        var harness = new Harness();
        using var service = harness.Create();
        Exception? reported = null;
        service.MonitoringFailed += (_, error) => reported = error;
        service.SetEnabled(true, true);
        var failure = new InvalidOperationException("WMI stopped");
        harness.Failed(failure);
        harness.Flush();
        Assert.Same(failure, reported);
        Assert.Equal(1, harness.Disposals);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public void SubscriptionStartupDriftDoesNotBecomeTheNewBaseline()
    {
        var harness = new Harness();
        harness.Subscribing = () => harness.Current = UuViewTarget.Host;
        using var service = harness.Create();
        service.SetEnabled(true, true);
        harness.Flush();
        Assert.Equal(new[] { UuViewTarget.Child }, harness.Requests);
    }

    private sealed class Harness
    {
        internal UuViewTarget Current = UuViewTarget.Child;
        internal Func<UuViewTarget, bool> NeedsCorrection = _ => true;
        internal Func<Task<UuViewSwitchService.Result>> Complete =
            () => Task.FromResult(new UuViewSwitchService.Result(true, "done"));
        internal List<UuViewTarget> Requests = [];
        internal CancellationToken LastToken;
        internal Queue<Action> Queue = new();
        internal Action Changed = () => { };
        internal Action<Exception> Failed = _ => { };
        internal int Subscriptions;
        internal int Disposals;
        internal bool FailSubscription;
        internal Action Subscribing = () => { };

        internal UuViewLockService Create() => new(
            () => Current, target => NeedsCorrection(target),
            (target, token) =>
            {
                Requests.Add(target);
                LastToken = token;
                return Complete();
            }, (changed, failed) =>
            {
                if (FailSubscription) throw new InvalidOperationException("subscription unavailable");
                Changed = changed;
                Failed = failed;
                Subscriptions++;
                Subscribing();
                return new Subscription(() => Disposals++);
            }, Queue.Enqueue);

        internal void Flush()
        {
            while (Queue.TryDequeue(out var action)) action();
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
