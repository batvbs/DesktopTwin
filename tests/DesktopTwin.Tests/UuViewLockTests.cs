using System.Windows.Threading;
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
    public async Task UnsuccessfulCorrectionAllowsNextTick(string outcome)
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
        await service.CheckAsync();

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

    private sealed class Harness
    {
        internal UuViewTarget Current = UuViewTarget.Child;
        internal Func<UuViewTarget, bool> NeedsCorrection = _ => true;
        internal Func<Task<UuViewSwitchService.Result>> Complete =
            () => Task.FromResult(new UuViewSwitchService.Result(true, "done"));
        internal List<UuViewTarget> Requests = [];
        internal CancellationToken LastToken;

        internal UuViewLockService Create() => new(
            Dispatcher.CurrentDispatcher, () => Current, target => NeedsCorrection(target),
            (target, token) =>
            {
                Requests.Add(target);
                LastToken = token;
                return Complete();
            });
    }
}
