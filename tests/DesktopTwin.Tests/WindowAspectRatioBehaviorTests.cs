using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.View.Behaviors;
using Microsoft.Xaml.Behaviors;

namespace DesktopTwin.Tests;

public sealed class WindowAspectRatioBehaviorTests
{
    [Fact]
    public void ResizeBurstQueuesOneCorrectionAndUsesLatestWidth() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        var events = 0;
        var callbacks = 0;
        fixture.Window.SizeChanged += (_, _) => events++;
        DispatcherHookEventHandler onPosted = (_, e) =>
        {
            if (e.Operation.Priority == DispatcherPriority.Loaded) callbacks++;
        };
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationPosted += onPosted;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                fixture.Window.Width = 500 + i;
                fixture.Window.UpdateLayout();
            }

            Assert.True(events > 1);
            Assert.Equal(1, callbacks);
        }
        finally
        {
            hooks.OperationPosted -= onPosted;
        }

        DrainDispatcher();
        Assert.InRange(fixture.Window.ActualWidth, 599 - 1, 599 + 1);
        fixture.AssertContentRatio();
    });

    [Fact]
    public void InteractiveResizeCancelsQueuedCorrectionAndNonDragResizeStillWorks() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Window.Width = 600;
        fixture.Window.UpdateLayout();
        SendMessage(fixture.Handle, 0x0231, 0, 0); // WM_ENTERSIZEMOVE
        fixture.SendSizing(800);

        fixture.Window.Height = 400;
        fixture.Window.UpdateLayout();
        DrainDispatcher();
        Assert.InRange(fixture.Window.ActualHeight, 400 - 1, 400 + 1);

        SendMessage(fixture.Handle, 0x0232, 0, 0); // WM_EXITSIZEMOVE
        fixture.Window.Width = 640;
        fixture.Window.UpdateLayout();
        DrainDispatcher();
        fixture.AssertContentRatio();
    });

    [Theory]
    [InlineData(WindowStyle.None)]
    [InlineData(WindowStyle.SingleBorderWindow)]
    public void FinalResizeEventAfterExitPreservesWmSizingRectangle(WindowStyle windowStyle) => RunOnSta(() =>
    {
        using var fixture = new WindowFixture(windowStyle: windowStyle);
        fixture.Window.Top = SystemParameters.WorkArea.Bottom - 180;
        SendMessage(fixture.Handle, 0x0231, 0, 0);
        var size = fixture.SendSizing(800);
        SendMessage(fixture.Handle, 0x0232, 0, 0);

        // WPF may deliver the last layout/SizeChanged after the native sizing loop exits.
        fixture.Window.Width = size.Width;
        fixture.Window.Height = size.Height;
        fixture.Window.UpdateLayout();
        DrainDispatcher();

        Assert.InRange(fixture.Window.ActualWidth, size.Width - 1, size.Width + 1);
        Assert.InRange(fixture.Window.ActualHeight, size.Height - 1, size.Height + 1);
        fixture.AssertContentRatio();
    });

    [Fact]
    public void MovingWithoutWmSizingStillAllowsSnapCorrection() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        SendMessage(fixture.Handle, 0x0231, 0, 0);
        fixture.Window.Width = 600;
        fixture.Window.Height = 400;
        fixture.Window.UpdateLayout();
        DrainDispatcher();
        fixture.AssertContentRatio();
        SendMessage(fixture.Handle, 0x0232, 0, 0);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedCorrectionRechecksWhetherResizeIsAllowed(bool disableBehavior) => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        var height = fixture.Window.Height;
        fixture.Window.Width = 600;
        fixture.Window.UpdateLayout();
        if (disableBehavior)
            fixture.Behavior.IsEnabled = false;
        else
            fixture.Window.ResizeMode = ResizeMode.NoResize;

        DrainDispatcher();
        Assert.InRange(fixture.Window.Height, height - 1, height + 1);
    });

    [Fact]
    public void DetachingCancelsQueuedCorrection() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        var height = fixture.Window.Height;
        fixture.Window.Width = 600;
        fixture.Window.UpdateLayout();
        Interaction.GetBehaviors(fixture.Content).Remove(fixture.Behavior);
        DrainDispatcher();

        Assert.InRange(fixture.Window.Height, height - 1, height + 1);
    });

    [Fact]
    public void ConfiguredRatioIsAppliedOnLoadAndAfterResolutionChange() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture(16d / 10d);
        fixture.AssertContentRatio();
        fixture.Behavior.AspectRatio = 4d / 3d;
        DrainDispatcher();
        fixture.AssertContentRatio();
    });

    [Fact]
    public void NonDragCorrectionStillFitsWorkAreaAndMaintainsRatio() => RunOnSta(() =>
    {
        using var fixture = new WindowFixture();
        var workArea = SystemParameters.WorkArea;
        fixture.Window.Top = workArea.Bottom - 180;
        fixture.Window.Width = 640;
        fixture.Window.Height = 400;
        fixture.Window.UpdateLayout();
        DrainDispatcher();

        Assert.True(fixture.Window.Top + fixture.Window.ActualHeight <= workArea.Bottom + 1);
        fixture.AssertContentRatio();
    });

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF test did not complete.");
        failure?.Throw();
    }

    private sealed class WindowFixture : IDisposable
    {
        internal Border Content { get; } = new();
        internal WindowAspectRatioBehavior Behavior { get; }
        internal Window Window { get; }
        internal nint Handle => new WindowInteropHelper(Window).Handle;

        internal WindowFixture(double ratio = 16d / 9d, WindowStyle windowStyle = WindowStyle.None)
        {
            Behavior = new WindowAspectRatioBehavior { IsEnabled = true, AspectRatio = ratio };
            Interaction.GetBehaviors(Content).Add(Behavior);
            Window = new Window
            {
                Width = 640,
                Height = 420,
                Left = SystemParameters.WorkArea.Left + 20,
                Top = SystemParameters.WorkArea.Top + 20,
                MinHeight = 100,
                MinWidth = 100,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0,
                WindowStyle = windowStyle,
                Content = Content
            };
            Window.Show();
            DrainDispatcher();
        }

        internal void AssertContentRatio() =>
            Assert.InRange(Math.Abs(Content.ActualWidth / Behavior.AspectRatio - Content.ActualHeight), 0, 1);

        internal Size SendSizing(int requestedPixelWidth)
        {
            Assert.True(WindowNativeMethods.TryGetWindowRect(Handle, out var rect));
            rect.Right = rect.Left + requestedPixelWidth;
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowNativeMethods.NativeRect>());
            try
            {
                Marshal.StructureToPtr(rect, pointer, false);
                Assert.Equal((nint)1, SendMessage(Handle, 0x0214, 2, pointer)); // WM_SIZING / WMSZ_RIGHT
                rect = Marshal.PtrToStructure<WindowNativeMethods.NativeRect>(pointer);
                var transform = PresentationSource.FromVisual(Window)!.CompositionTarget!.TransformToDevice;
                return new Size((rect.Right - rect.Left) / transform.M11, (rect.Bottom - rect.Top) / transform.M22);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public void Dispose() => Window.Close();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
