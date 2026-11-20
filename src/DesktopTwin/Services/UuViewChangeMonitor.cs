using System;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopTwin.Services;

/// <summary>订阅全部会话变更和 UU 串流端启动事件；没有周期查询。</summary>
internal sealed class UuViewChangeMonitor : IDisposable
{
    private const int WmSessionChange = 0x02B1;
    private readonly Dispatcher _dispatcher;
    private readonly Action _changed;
    private readonly Action<Exception> _failed;
    private HwndSource? _window;
    private ManagementEventWatcher? _watcher;
    private bool _registered;
    private volatile bool _disposed;

    internal UuViewChangeMonitor(Dispatcher dispatcher, Action changed, Action<Exception> failed)
    {
        _dispatcher = dispatcher;
        _changed = changed;
        _failed = failed;
        try
        {
            _window = new HwndSource(new HwndSourceParameters("DesktopTwin.UU.SessionEvents")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE，无可见窗口。
                WindowStyle = 0,
                Width = 0,
                Height = 0
            });
            _window.AddHook(OnWindowMessage);
            if (!WTSRegisterSessionNotification(_window.Handle, 1)) // NOTIFY_FOR_ALL_SESSIONS
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _registered = true;
            // ProcessStartTrace 由事件提供者推送；不用 WITHIN / __InstanceCreationEvent 轮询。
            _watcher = new ManagementEventWatcher(
                "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'GameViewerServer.exe'");
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && message == WmSessionChange)
        {
            _changed();
        }

        return IntPtr.Zero;
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e) => Post(_changed);

    private void OnWatcherStopped(object sender, StoppedEventArgs e) =>
        Post(() => _failed(new InvalidOperationException($"UU 串流事件订阅已停止：{e.Status}")));

    private void Post(Action action)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            }));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EventArrived -= OnProcessStarted;
            _watcher.Stopped -= OnWatcherStopped;
            try
            {
                _watcher.Dispose();
            }
            catch (Exception exception) when (exception is ManagementException or COMException)
            {
                AppLog.Warn("释放 UU 串流事件订阅失败。", exception);
            }
        }

        if (_window is not null)
        {
            if (_registered)
            {
                _ = WTSUnRegisterSessionNotification(_window.Handle);
            }

            _window.RemoveHook(OnWindowMessage);
            _window.Dispose();
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
