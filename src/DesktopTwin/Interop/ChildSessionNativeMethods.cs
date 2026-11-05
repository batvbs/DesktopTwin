using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace DesktopTwin.Interop;

internal static class ChildSessionNativeMethods
{
    private const int DefaultRdpPort = 3389;
    private const int ErrorNotFound = 1168;
    private const string RdpTcpRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string TermServiceParametersRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string RdpWrapperLibraryName = "rdpwrap.dll";
    private const uint NoChildSessionId = uint.MaxValue;
    private static readonly IntPtr CurrentServerHandle = IntPtr.Zero;
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> RdpInputWindows = new();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr serverHandle,
        uint sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumChildWindowCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumLength);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(
        IntPtr window,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    internal static void EnableChildSessions()
    {
        if (!WTSEnableChildSessions(true))
        {
            throw CreateLastWin32Exception("无法启用 RDP Child Session");
        }
    }

    internal static int GetConfiguredRdpPort()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var rdpTcpKey = localMachine.OpenSubKey(RdpTcpRegistryPath);
            var configuredPort = rdpTcpKey?.GetValue("PortNumber");
            return configuredPort is int port and > 0 and <= ushort.MaxValue
                ? port
                : DefaultRdpPort;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return DefaultRdpPort;
        }
    }

    internal static bool IsRdpWrapperEnabled()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var termServiceParametersKey =
                localMachine.OpenSubKey(TermServiceParametersRegistryPath);
            var serviceLibraryPath =
                termServiceParametersKey?.GetValue("ServiceDll") as string;
            return serviceLibraryPath?.Contains(
                RdpWrapperLibraryName,
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }

    internal static bool IsChildSessionsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
        {
            throw CreateLastWin32Exception("无法读取 RDP Child Session 状态");
        }

        return enabled;
    }

    /// <summary>
    /// 关闭 Windows 的 Child Sessions 功能。
    ///
    /// 该开关是系统级持久设置，关闭后本机不再支持创建第二个桌面会话。
    /// 只有在用户明确要求「恢复系统原状」时才调用。
    /// </summary>
    internal static void DisableChildSessions()
    {
        if (!WTSEnableChildSessions(false))
        {
            throw CreateLastWin32Exception("无法关闭 RDP Child Session");
        }
    }

    internal static uint? TryGetChildSessionId()
    {
        return WTSGetChildSessionId(out var sessionId) && sessionId != NoChildSessionId
            ? sessionId
            : null;
    }

    /// <summary>
    /// 枚举本机全部会话。
    ///
    /// 失败时返回空数组而不是抛异常：调用方（UU 视角切换）在切换过程中会被别的进程
    /// 反复改变会话状态，查询失败是常态，不该让一次查询失败打断整个切换流程。
    /// </summary>
    internal static IReadOnlyList<SessionInfo> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(CurrentServerHandle, 0, 1, out var buffer, out var count)
            || buffer == IntPtr.Zero)
        {
            return [];
        }

        var sessions = new List<SessionInfo>(count);
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WtsSessionInfo>(
                    buffer + (index * size));

                // 会话号大于 65535 的是伪会话（例如 Services），不参与判断。
                if (item.SessionId > ushort.MaxValue)
                {
                    continue;
                }

                sessions.Add(new SessionInfo(
                    (uint)item.SessionId,
                    item.WinStationName == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUni(item.WinStationName) ?? string.Empty,
                    item.State,
                    QuerySessionUserName((uint)item.SessionId)));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return sessions;
    }

    /// <summary>
    /// 取「真正接着显示器」的那个会话。
    ///
    /// 先按 WinStation 名找 Console：<c>WTSGetActiveConsoleSessionId</c> 在会话切换的瞬间
    /// 会短暂返回别的值（甚至 0xFFFFFFFF），只靠它会认错会话。
    /// </summary>
    internal static uint? TryGetConsoleSessionId()
    {
        foreach (var session in EnumerateSessions())
        {
            if (string.Equals(session.WinStationName, "Console", StringComparison.OrdinalIgnoreCase))
            {
                return session.SessionId;
            }
        }

        return null;
    }

    /// <summary>
    /// 查询会话的连接状态。会话不存在时返回 -1。
    /// </summary>
    internal static int GetSessionState(uint sessionId)
    {
        foreach (var session in EnumerateSessions())
        {
            if (session.SessionId == sessionId)
            {
                return session.State;
            }
        }

        return -1;
    }

    /// <summary>
    /// 断开一个会话但不注销它：桌面上的程序继续运行，会话进入 Disconnected 状态。
    ///
    /// 这是 UU 视角切换「去整台电脑」的关键一步——UU 的被控端由 SYSTEM 服务拉起，
    /// 只会挑会话号最大的已登录桌面，把分身断掉它就只能落到 Console。
    /// </summary>
    internal static bool DisconnectSession(uint sessionId, bool wait = false)
    {
        return WTSDisconnectSession(CurrentServerHandle, sessionId, wait);
    }

    /// <summary>
    /// 会话状态码是否为「已连接（Active）」，即桌面上真的有人。
    /// </summary>
    internal static bool IsSessionActive(uint sessionId) => GetSessionState(sessionId) == 0;

    /// <summary>
    /// 读会话的登录用户名。取不到时返回空串。
    /// </summary>
    private static string QuerySessionUserName(uint sessionId)
    {
        const int userNameInfoClass = 5;

        if (!WTSQuerySessionInformationW(
                CurrentServerHandle,
                sessionId,
                userNameInfoClass,
                out var buffer,
                out _)
            || buffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static uint? TerminateChildSession()
    {
        return TerminateChildSession(expectedSessionId: null, wait: true);
    }

    internal static uint? TerminateChildSession(bool wait)
    {
        return TerminateChildSession(expectedSessionId: null, wait: wait);
    }

    /// <summary>
    /// 仅当当前 Child Session 与预期标识一致时注销会话，避免旧界面操作误伤新会话。
    /// </summary>
    /// <param name="expectedSessionId">用户请求关闭的 Child Session 标识。</param>
    internal static uint? TerminateChildSession(uint expectedSessionId)
    {
        return TerminateChildSession(expectedSessionId, wait: true);
    }

    /// <summary>
    /// 仅当当前 Child Session 与预期标识一致时注销会话，并指定是否等待注销完成。
    /// 退出流程用 <c>wait: false</c>，避免阻塞进程关闭。
    /// </summary>
    /// <param name="expectedSessionId">需要匹配的会话标识。</param>
    /// <param name="wait">是否等待注销操作完成。</param>
    internal static uint? TerminateChildSession(uint expectedSessionId, bool wait)
    {
        return TerminateChildSession((uint?)expectedSessionId, wait);
    }

    /// <summary>
    /// 查询并注销当前 Child Session，可选校验预期会话标识。
    /// </summary>
    /// <param name="expectedSessionId">需要匹配的会话标识；为空时注销当前 Child Session。</param>
    /// <param name="wait">是否等待注销操作完成。</param>
    private static uint? TerminateChildSession(uint? expectedSessionId, bool wait)
    {
        if (!WTSGetChildSessionId(out var childSessionId))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(
                error,
                $"无法取得 RDP Child Session ID（Win32 错误 {error}）");
        }

        if (childSessionId == NoChildSessionId)
        {
            return null;
        }

        if (expectedSessionId is { } expected && childSessionId != expected)
        {
            throw new InvalidOperationException(
                $"桌面分身会话已从 {expected} 变更为 {childSessionId}，已取消注销操作。");
        }

        if (!WTSLogoffSession(CurrentServerHandle, childSessionId, wait))
        {
            throw CreateLastWin32Exception($"无法注销 Child Session {childSessionId}");
        }

        return childSessionId;
    }

    internal static bool TryFocusRdpInputWindow(IntPtr rdpHostWindow)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow);
        if (inputWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = SetFocus(inputWindow);
        return GetFocus() == inputWindow;
    }

    internal static bool IsRdpInputWindowFocused(IntPtr rdpHostWindow)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow);
        return inputWindow != IntPtr.Zero && GetFocus() == inputWindow;
    }

    /// <summary>
    /// 禁用或恢复 RDP 的输入窗口。禁用后本机鼠标与键盘都不会进入桌面分身，
    /// 画面渲染不受影响。输入窗口尚未创建时不做任何处理。
    /// </summary>
    internal static void SetRdpInputWindowEnabled(IntPtr rdpHostWindow, bool enabled)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow);
        if (inputWindow != IntPtr.Zero)
        {
            _ = EnableWindow(inputWindow, enabled);
        }
    }

    internal static void ClearRdpInputWindowCache(IntPtr rdpHostWindow)
    {
        RdpInputWindows.TryRemove(rdpHostWindow, out _);
    }

    private static IntPtr FindRdpInputWindow(IntPtr rdpHostWindow)
    {
        if (rdpHostWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (RdpInputWindows.TryGetValue(rdpHostWindow, out var cachedInputWindow))
        {
            if (IsWindow(cachedInputWindow) && IsChild(rdpHostWindow, cachedInputWindow))
            {
                return cachedInputWindow;
            }

            RdpInputWindows.TryRemove(rdpHostWindow, out _);
        }

        var inputWindow = IntPtr.Zero;
        EnumChildWindowCallback callback = (window, _) =>
        {
            const int windowTextCapacity = 256;
            var windowText = new StringBuilder(windowTextCapacity);
            _ = GetWindowText(window, windowText, windowText.Capacity);
            if (!string.Equals(
                    windowText.ToString(),
                    "Input Capture Window",
                    StringComparison.Ordinal))
            {
                return true;
            }

            inputWindow = window;
            return false;
        };

        _ = EnumChildWindows(rdpHostWindow, callback, IntPtr.Zero);
        if (inputWindow != IntPtr.Zero)
        {
            RdpInputWindows[rdpHostWindow] = inputWindow;
        }
        return inputWindow;
    }

    private static Win32Exception CreateLastWin32Exception(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation}（Win32 错误 {error}）");
    }

    private delegate bool EnumChildWindowCallback(IntPtr window, IntPtr parameter);

    /// <summary>
    /// <c>WTS_SESSION_INFO</c> 的托管映射。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        internal int SessionId;
        internal IntPtr WinStationName;
        internal int State;
    }
}

/// <summary>
/// 一个 Windows 会话的快照。
/// </summary>
/// <param name="SessionId">会话号。</param>
/// <param name="WinStationName">WinStation 名，Console 表示接显示器的那个会话。</param>
/// <param name="State">连接状态码，0 表示 Active。</param>
/// <param name="UserName">登录用户名，未登录时为空串。</param>
internal readonly record struct SessionInfo(
    uint SessionId,
    string WinStationName,
    int State,
    string UserName);
