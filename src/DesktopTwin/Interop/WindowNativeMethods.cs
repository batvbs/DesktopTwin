using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTwin.Interop;

/// <summary>
/// 窗口相关的 Win32 调用。只保留本项目实际用到的少量 API，不引入额外的 P/Invoke 包装库。
/// </summary>
internal static class WindowNativeMethods
{
    /// <summary>
    /// 设置窗口位置时不改变尺寸。
    /// </summary>
    internal const uint SwpNoSize = 0x0001;

    /// <summary>
    /// 设置窗口位置时不改变 Z 序。
    /// </summary>
    internal const uint SwpNoZOrder = 0x0004;

    /// <summary>
    /// 设置窗口位置时不激活窗口。
    /// </summary>
    internal const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 设置窗口位置时不改变所有者窗口的 Z 序。
    /// </summary>
    internal const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>
    /// 取窗口在屏幕坐标系下的矩形（含边框）。
    /// </summary>
    internal static bool TryGetWindowRect(IntPtr window, out NativeRect rect)
    {
        if (window == IntPtr.Zero)
        {
            rect = default;
            return false;
        }

        return GetWindowRect(window, out rect);
    }

    /// <summary>
    /// 移动窗口到指定屏幕坐标，不改变尺寸与 Z 序。
    /// </summary>
    internal static void MoveWindow(IntPtr window, int left, int top)
    {
        _ = SetWindowPos(
            window,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>
    /// 按窗口类名查找顶层窗口，找不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    /// <param name="className">窗口类名。</param>
    /// <param name="titleContains">可选的标题关键字，用于区分同类的不同窗口。</param>
    internal static IntPtr FindWindowByClassName(string className, string? titleContains = null)
    {
        if (string.IsNullOrEmpty(className))
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        _ = EnumWindows(
            (window, _) =>
            {
                if (!IsWindowVisible(window))
                {
                    return true;
                }

                var buffer = new StringBuilder(256);
                _ = GetClassName(window, buffer, buffer.Capacity);
                if (!string.Equals(buffer.ToString(), className, StringComparison.Ordinal))
                {
                    return true;
                }

                if (titleContains is not null)
                {
                    var title = new StringBuilder(256);
                    _ = GetWindowText(window, title, title.Capacity);
                    if (!title.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                found = window;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 取窗口所在显示器的完整矩形（含任务栏区域），单位是物理像素。
    ///
    /// 全屏要用显示器全区域而不是工作区：工作区已经扣掉了任务栏，
    /// 用它做全屏会留一条任务栏的位置，看起来就不是全屏。
    /// </summary>
    internal static bool TryGetMonitorRect(IntPtr window, out NativeRect rect)
    {
        rect = default;

        if (window == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        rect = info.Monitor;
        return true;
    }

    /// <summary>
    /// 取鼠标在屏幕坐标系下的位置（物理像素）。
    /// </summary>
    internal static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumLength);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    /// <summary>
    /// 屏幕坐标矩形。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        /// <summary>左边界。</summary>
        public int Left;

        /// <summary>上边界。</summary>
        public int Top;

        /// <summary>右边界。</summary>
        public int Right;

        /// <summary>下边界。</summary>
        public int Bottom;
    }
}
