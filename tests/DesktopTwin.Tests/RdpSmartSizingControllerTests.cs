using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpSmartSizingControllerTests
{
    [Fact]
    public void RepeatedSettingWritesOnceAndQueuesOneRefresh()
    {
        var writes = new List<bool>();
        var callbacks = new Queue<Action>();
        var refreshes = 0;
        var controller = new RdpSmartSizingController(writes.Add, callbacks.Enqueue, () => refreshes++);

        for (var i = 0; i < 100; i++)
        {
            Set(controller, true);
        }

        Assert.Equal(new[] { true }, writes);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void RapidChangesApplyLatestValueAndRefreshLatestSizeOnce()
    {
        var callbacks = new Queue<Action>();
        var applied = false;
        var width = 640;
        var observed = new List<(bool Enabled, int Width)>();
        var controller = new RdpSmartSizingController(
            value => applied = value,
            callbacks.Enqueue,
            () => observed.Add((applied, width)));

        Set(controller, true);
        Set(controller, false);
        Set(controller, true);
        width = 1920;
        Assert.Single(callbacks);
        callbacks.Dequeue()();

        Assert.Equal(new[] { (true, 1920) }, observed);
        Set(controller, false);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal((false, 1920), observed[1]);
    }

    [Fact]
    public void FailedWriteCanRetrySameValue()
    {
        var attempts = 0;
        var controller = new RdpSmartSizingController(
            _ =>
            {
                if (++attempts == 1) throw new InvalidOperationException("COM write failed");
            },
            _ => { },
            () => { });

        Assert.Throws<InvalidOperationException>(() => controller.Apply(true));
        Assert.True(controller.Apply(true));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void FailedChangeDoesNotTrustPreviousComValue()
    {
        var writes = new List<bool>();
        var controller = new RdpSmartSizingController(
            value =>
            {
                writes.Add(value);
                if (!value) throw new InvalidOperationException("COM state uncertain");
            },
            _ => { },
            () => { });

        controller.Apply(true);
        Assert.Throws<InvalidOperationException>(() => controller.Apply(false));
        Assert.True(controller.Apply(true));
        Assert.Equal(new[] { true, false, true }, writes);
    }

    [Fact]
    public void ResetReappliesSameSettingAndDiscardsOldCallback()
    {
        var writes = new List<bool>();
        var callbacks = new Queue<Action>();
        var refreshes = 0;
        var controller = new RdpSmartSizingController(writes.Add, callbacks.Enqueue, () => refreshes++);

        Set(controller, true);
        controller.Reset();
        Set(controller, true);
        Assert.Equal(new[] { true, true }, writes);
        Assert.Equal(2, callbacks.Count);

        callbacks.Dequeue()();
        Assert.Equal(0, refreshes);
        controller.ScheduleRefresh();
        Assert.Single(callbacks); // The old callback must not clear the new pending refresh.
        callbacks.Dequeue()();
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void FailedSchedulingCanRetryThroughSettingEntryPoint()
    {
        var attempts = 0;
        var callbacks = new Queue<Action>();
        var controller = new RdpSmartSizingController(
            _ => { },
            callback =>
            {
                if (++attempts == 1) throw new InvalidOperationException("Handle unavailable");
                callbacks.Enqueue(callback);
            },
            () => { });

        Assert.Throws<InvalidOperationException>(() => Set(controller, true));
        Set(controller, true);
        Assert.Single(callbacks);
    }

    [Fact]
    public void FailedRefreshCanRetryThroughSettingEntryPoint()
    {
        var attempts = 0;
        var callbacks = new Queue<Action>();
        var controller = new RdpSmartSizingController(
            _ => { },
            callbacks.Enqueue,
            () =>
            {
                if (++attempts == 1) throw new InvalidOperationException("Resize failed");
            });

        Set(controller, true);
        Assert.Throws<InvalidOperationException>(callbacks.Dequeue());
        Set(controller, true);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal(2, attempts);
    }

    private static void Set(RdpSmartSizingController controller, bool enabled)
    {
        if (controller.Apply(enabled)) controller.ScheduleRefresh();
    }
}
