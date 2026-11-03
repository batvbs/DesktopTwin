using System;
using System.Runtime.InteropServices;

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

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
