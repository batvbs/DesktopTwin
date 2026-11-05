using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopTwin.Interop;

/// <summary>
/// 进程相关的 Win32 调用。
///
/// 主要用途是结束 UU 的串流端 <c>GameViewerServer.exe</c>——它由 SYSTEM 服务拉起，
/// 属于高权限进程，普通管理员令牌默认拿不到它的句柄，需要先启用 <c>SeDebugPrivilege</c>。
/// </summary>
internal static class ProcessNativeMethods
{
    private const uint ProcessTerminate = 0x0001;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const string SeDebugPrivilege = "SeDebugPrivilege";

    /// <summary>
    /// 启用当前进程的 <c>SeDebugPrivilege</c>。失败不抛异常——拿不到就退回普通结束流程。
    /// </summary>
    /// <returns>成功启用返回 true。</returns>
    internal static bool TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SeDebugPrivilege, out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            return AdjustTokenPrivileges(
                token,
                disableAllPrivileges: false,
                ref privileges,
                bufferLength: 0,
                previousState: IntPtr.Zero,
                returnLength: IntPtr.Zero)
                && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>
    /// 结束指定进程。
    /// </summary>
    /// <param name="processId">进程标识。</param>
    /// <param name="errorCode">失败时的 Win32 错误码。</param>
    /// <returns>成功返回 true。</returns>
    internal static bool TryTerminateProcess(int processId, out int errorCode)
    {
        errorCode = 0;
        var handle = OpenProcess(ProcessTerminate, inheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (!TerminateProcess(handle, exitCode: 1))
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            return true;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
