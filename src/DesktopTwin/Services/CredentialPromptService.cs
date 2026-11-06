using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 触发一次 Windows 原生的凭据输入框，用于校验密码是否有效。
///
/// 为什么不用「断开重连分身」来触发：那会打断用户正在用的会话。
/// 这里用 <c>CredUIPromptForWindowsCredentials</c>——它弹出的是同一个
/// 「Windows 安全中心」凭据框，走的是同一条凭据提供程序链路，
/// 但完全不碰远程桌面会话。
///
/// 弹窗在独立的 STA 线程上创建：CredUI 是阻塞式的模态对话框，
/// 直接调在 WPF 界面线程上会让主窗口卡住，而主窗口里嵌着分身画面。
/// </summary>
internal static class CredentialPromptService
{
    private const int ErrorCancelled = 1223;
    private const uint CredUiWinGeneric = 0x00000001;
    private const uint CredUiWinCheckBox = 0x00000002;

    /// <summary>
    /// 弹窗结果。
    /// </summary>
    /// <param name="Success">拿到了凭据。</param>
    /// <param name="Canceled">用户主动取消。</param>
    /// <param name="UserName">用户名。</param>
    /// <param name="Domain">域。</param>
    /// <param name="Password">密码。</param>
    /// <param name="Message">失败说明。</param>
    internal readonly record struct PromptResult(
        bool Success,
        bool Canceled,
        string UserName,
        string Domain,
        string Password,
        string Message);

    /// <summary>
    /// 弹出凭据框并等待用户（或自动填充）提交。
    /// </summary>
    /// <param name="ownerWindow">父窗口句柄，让弹窗落在主窗口前面。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="cancellationToken">取消标记。</param>
    internal static Task<PromptResult> PromptAsync(
        IntPtr ownerWindow,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Prompt(ownerWindow));
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or DllNotFoundException)
            {
                completion.TrySetResult(new PromptResult(
                    false,
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    exception.Message));
            }
        })
        {
            IsBackground = true,
            Name = "DesktopTwin.CredentialPrompt"
        };

        // CredUI 的对话框需要 STA。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return AwaitWithTimeoutAsync(completion.Task, timeout, cancellationToken);
    }

    private static async Task<PromptResult> AwaitWithTimeoutAsync(
        Task<PromptResult> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var delay = Task.Delay(timeout, cancellationToken);
        var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);

        if (ReferenceEquals(finished, task))
        {
            return await task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new PromptResult(
            false,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            Locale.Get("Cred.Timeout"));
    }

    private static PromptResult Prompt(IntPtr ownerWindow)
    {
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            ParentWindow = ownerWindow,
            MessageText = Locale.Get("Cred.MessageText"),
            CaptionText = Locale.Get("Cred.CaptionText")
        };

        uint authPackage = 0;
        var save = false;

        var result = CredUIPromptForWindowsCredentialsW(
            ref info,
            0,
            ref authPackage,
            IntPtr.Zero,
            0,
            out var outBuffer,
            out var outBufferSize,
            ref save,
            CredUiWinGeneric | CredUiWinCheckBox);

        if (result == ErrorCancelled)
        {
            return new PromptResult(false, true, string.Empty, string.Empty, string.Empty, Locale.Get("Cred.Canceled"));
        }

        if (result != 0)
        {
            return new PromptResult(
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                Locale.Format("Cred.OpenFailed", result));
        }

        try
        {
            return Unpack(outBuffer, outBufferSize);
        }
        finally
        {
            if (outBuffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(outBuffer);
            }
        }
    }

    private static PromptResult Unpack(IntPtr buffer, uint size)
    {
        const int capacity = 512;

        var userName = new StringBuilder(capacity);
        var domain = new StringBuilder(capacity);
        var password = new StringBuilder(capacity);

        var userNameLength = (uint)capacity;
        var domainLength = (uint)capacity;
        var passwordLength = (uint)capacity;

        if (!CredUnPackAuthenticationBufferW(
                0,
                buffer,
                size,
                userName,
                ref userNameLength,
                domain,
                ref domainLength,
                password,
                ref passwordLength))
        {
            var error = Marshal.GetLastWin32Error();
            return new PromptResult(
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                Locale.Format("Cred.ReadFailed", error));
        }

        return new PromptResult(
            true,
            false,
            userName.ToString(),
            domain.ToString(),
            password.ToString(),
            string.Empty);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        internal int Size;
        internal IntPtr ParentWindow;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? MessageText;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? CaptionText;
        internal IntPtr Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredUIPromptForWindowsCredentialsW(
        ref CredUiInfo info,
        int authError,
        ref uint authPackage,
        IntPtr inAuthBuffer,
        uint inAuthBufferSize,
        out IntPtr outAuthBuffer,
        out uint outAuthBufferSize,
        ref bool save,
        uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBufferW(
        uint flags,
        IntPtr authBuffer,
        uint authBufferSize,
        StringBuilder userName,
        ref uint userNameLength,
        StringBuilder domain,
        ref uint domainLength,
        StringBuilder password,
        ref uint passwordLength);
}
