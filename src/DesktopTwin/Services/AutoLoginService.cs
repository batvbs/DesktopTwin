using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using DesktopTwin.Interop;
using DesktopTwin.Models;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 分身自动登录。
///
/// 两条路，先用简单的那条：
/// 1. 连接前把用户名/密码直接交给 RDP 客户端（<c>IMsRdpClientNonScriptable::put_ClearTextPassword</c>），
///    正常情况下面向本机的凭据交换是静默的，根本不会弹窗；
/// 2. 有些环境（策略、Windows Hello、账户类型）仍会弹「Windows 安全中心」的凭据框，
///    而且有时连用户名一起要。这时用 UI Automation 把用户名和密码填进去并点确定。
///
/// 第 2 条是兜底，不是主路径——它依赖对方的 UI 结构，属于「能填就填，填不了就让人自己输」，
/// 所以有严格的重试上限，绝不无限重试。
/// </summary>
internal sealed class AutoLoginService : IDisposable
{
    /// <summary>
    /// 「Windows 安全中心」凭据框的窗口类名。这个类名是实测得到的，
    /// 换系统版本可能变；变了只会导致兜底失效，不影响第 1 条路径。
    /// </summary>
    private const string CredentialDialogClassName = "Credential Dialog Xaml Host";

    /// <summary>
    /// 经典 Win32 凭据对话框的类名。
    /// </summary>
    private const string LegacyDialogClassName = "#32770";

    /// <summary>
    /// 经典对话框靠标题区分，中英文各取一个关键字。
    /// </summary>
    private static readonly string[] LegacyDialogTitleKeywords = ["安全", "Security", "凭据", "Credential"];

    /// <summary>
    /// Windows 自动登录的配置位置。只读取状态，不读取、不写入密码。
    /// </summary>
    private const string WinlogonRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>
    /// 微软账户在本机的身份信息。子键名就是账户邮箱，用于 UPN 形式的登录。
    /// </summary>
    private const string MicrosoftAccountPropertiesPath =
        @"Software\Microsoft\IdentityCRL\UserExtendedProperties";

    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string userName,
        string domain,
        string password,
        int logonType,
        int logonProvider,
        out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 一次连接最多自动填几次。凭据框反复出现通常意味着密码本身就是错的，
    /// 继续填只会把账户往锁定策略上推。
    /// </summary>
    private const int MaxFillAttempts = 2;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan WatchDuration = TimeSpan.FromSeconds(60);

    private readonly SettingsService _settings;
    private CancellationTokenSource? _watchCancellation;
    private IntPtr _lastFilledDialog;
    private bool _disposed;

    /// <summary>
    /// 创建自动登录服务。
    /// </summary>
    /// <param name="settings">设置来源。</param>
    public AutoLoginService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 用户是否打开了自动登录开关。
    /// </summary>
    public bool IsEnabled => _settings.Current.Automation.AutoLoginEnabled;

    /// <summary>
    /// 是否已经存了密码（且能解开）。
    /// </summary>
    public bool HasStoredPassword => !string.IsNullOrEmpty(ReadPassword());

    /// <summary>
    /// 自动登录使用的用户名。留空时取当前 Windows 账户。
    /// </summary>
    public string UserName
    {
        get
        {
            var configured = _settings.Current.Automation.AutoLoginUserName;
            return string.IsNullOrWhiteSpace(configured) ? Environment.UserName : configured.Trim();
        }
    }

    /// <summary>
    /// 自动登录使用的域。本地账户就是机器名。
    /// </summary>
    public string Domain => Environment.UserDomainName;

    /// <summary>
    /// 保存密码（DPAPI 加密后写进设置）。
    /// </summary>
    /// <returns>加密失败时返回 false。</returns>
    public bool SavePassword(string plainText)
    {
        ThrowIfDisposed();
        var protectedValue = DpapiProtector.Protect(plainText);
        if (protectedValue is null)
        {
            return false;
        }

        _settings.Current.Automation.AutoLoginPassword = protectedValue;
        _settings.SaveSoon();
        return true;
    }

    /// <summary>
    /// 清除已保存的密码。
    /// </summary>
    public void ClearPassword()
    {
        ThrowIfDisposed();
        _settings.Current.Automation.AutoLoginPassword = null;
        _settings.SaveSoon();
    }

    /// <summary>
    /// 密码校验结论。
    /// </summary>
    public enum PasswordCheckResult
    {
        /// <summary>密码当前有效。</summary>
        Valid,

        /// <summary>用户名或密码错误。</summary>
        InvalidPassword,

        /// <summary>账户被禁用。</summary>
        AccountDisabled,

        /// <summary>密码已过期。</summary>
        PasswordExpired,

        /// <summary>账户限制（空密码、登录时段、工作站限制）。</summary>
        AccountRestricted,

        /// <summary>无法判定（权限不足或未知错误）。</summary>
        Unknown
    }

    /// <summary>
    /// 校验一组凭据当前是否有效。
    ///
    /// 用 <c>LogonUser</c> 做一次网络登录验证，不产生界面、不创建进程。
    /// 错误码能把「密码错了」和「账户被禁用 / 密码过期」区分开，比让用户反复试密码有用。
    /// </summary>
    /// <param name="password">待校验的密码。</param>
    /// <param name="errorCode">Win32 错误码，成功时为 0。</param>
    public PasswordCheckResult VerifyPassword(string password, out int errorCode)
    {
        var (userName, domain) = ResolveLogonIdentity();
        return VerifyPassword(userName, domain, password, out errorCode);
    }

    /// <summary>
    /// 用指定的用户名/域校验凭据。用于校验「凭据框里实际提交的那一组」。
    /// </summary>
    /// <param name="userName">用户名。</param>
    /// <param name="domain">域；本地账户留空即可。</param>
    /// <param name="password">密码。</param>
    /// <param name="errorCode">Win32 错误码，成功时为 0。</param>
    public PasswordCheckResult VerifyPassword(
        string userName,
        string domain,
        string password,
        out int errorCode)
    {
        errorCode = 0;

        if (LogonUserW(
                userName,
                domain,
                password,
                Logon32LogonNetwork,
                Logon32ProviderDefault,
                out var token))
        {
            if (token != IntPtr.Zero)
            {
                _ = CloseHandle(token);
            }

            return PasswordCheckResult.Valid;
        }

        errorCode = Marshal.GetLastPInvokeError();
        return errorCode switch
        {
            1326 => PasswordCheckResult.InvalidPassword,
            1331 => PasswordCheckResult.AccountDisabled,
            1330 => PasswordCheckResult.PasswordExpired,
            1327 or 1385 => PasswordCheckResult.AccountRestricted,
            _ => PasswordCheckResult.Unknown
        };
    }

    /// <summary>
    /// 尝试读取当前账户的微软账户邮箱。
    ///
    /// 该邮箱是 <c>runas /u:MicrosoftAccount\&lt;邮箱&gt;</c> 与 <c>LogonUser</c> 需要的用户名，
    /// 本地用户名（C:\Users 下那个）并不是它。
    /// </summary>
    /// <param name="email">读到的邮箱。</param>
    public bool TryGetMicrosoftAccountEmail(out string email)
    {
        email = string.Empty;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MicrosoftAccountPropertiesPath);
            if (key is null)
            {
                return false;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (name.Contains('@'))
                {
                    email = name;
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("读取微软账户邮箱失败。", exception);
        }

        return false;
    }

    /// <summary>
    /// 生成「刷新本机凭据缓存」的命令，供用户复制执行。
    ///
    /// 对应社区做法：<c>runas /u:MicrosoftAccount\&lt;邮箱&gt; winver</c>，
    /// 用新密码登录一次，让 Windows 把本地缓存的凭据验证器更新过来。
    /// 输入密码时命令行不会有任何提示，属正常现象。
    /// </summary>
    public string BuildPasswordSyncCommand()
    {
        var identity = TryGetMicrosoftAccountEmail(out var email)
            ? $@"MicrosoftAccount\{email}"
            : $@"{Environment.MachineName}\{Environment.UserName}";

        return $"runas /u:{identity} winver";
    }

    /// <summary>
    /// 决定 <c>LogonUser</c> 用哪个用户名/域：微软账户用 UPN + MicrosoftAccount，本地账户用机器名。
    /// </summary>
    private (string UserName, string Domain) ResolveLogonIdentity()
    {
        if (TryGetMicrosoftAccountEmail(out var email))
        {
            return (email, "MicrosoftAccount");
        }

        return (Environment.UserName, Environment.MachineName);
    }

    /// <summary>
    /// 取出一组可用于 RDP 静默登录的凭据。开关关闭或没存密码时返回 false。
    /// </summary>
    public bool TryGetCredential(out RdpCredential credential)
    {
        credential = default;
        if (!IsEnabled)
        {
            return false;
        }

        var password = ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        credential = new RdpCredential(UserName, Domain, password);
        return true;
    }

    /// <summary>
    /// 读 Windows 自己的自动登录是否开启（<c>AutoAdminLogon</c>）。
    ///
    /// 只读状态，不读密码：那个值在注册表里是可还原的，程序没有任何理由去碰它。
    /// </summary>
    /// <param name="userName">已配置的默认登录账户；读不到时为空串。</param>
    public bool IsSystemAutoLoginEnabled(out string userName)
    {
        userName = string.Empty;

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var winlogon = localMachine.OpenSubKey(WinlogonRegistryPath);
            if (winlogon is null)
            {
                return false;
            }

            userName = winlogon.GetValue("DefaultUserName") as string ?? string.Empty;
            return winlogon.GetValue("AutoAdminLogon") is string value && value == "1";
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("读取系统自动登录状态失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 打开 Windows 的「用户账户」对话框（<c>netplwiz</c>），让用户自己配置系统级自动登录。
    ///
    /// 为什么不由程序代劳：这一步等于让本机开机即进桌面，属于整机安全策略，
    /// 必须由用户在系统原生的界面里自己确认，程序只负责把人送到门口。
    /// </summary>
    /// <param name="error">失败原因。</param>
    /// <returns>成功拉起进程返回 true。</returns>
    public bool TryOpenSystemAutoLoginSettings(out string error)
    {
        error = string.Empty;

        // 用绝对路径：UseShellExecute 下虽然不做 PATH 搜索，
        // 但显式写全路径能避免被同名文件误导，和本程序其他启动点保持一致。
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var netplwizPath = Path.Combine(systemDirectory, "netplwiz.exe");
        if (!File.Exists(netplwizPath))
        {
            error = "找不到 netplwiz.exe，请手动按 Win+R 输入 netplwiz 打开。";
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = netplwizPath,
                UseShellExecute = true
            });

            if (process is null)
            {
                error = "系统设置没有启动起来，请手动按 Win+R 输入 netplwiz。";
                return false;
            }

            AppLog.Info("已打开系统自动登录设置（netplwiz）。");
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or FileNotFoundException)
        {
            AppLog.Warn("打开 netplwiz 失败。", exception);
            error = $"无法打开系统设置：{exception.GetBaseException().Message}";
            return false;
        }
    }

    /// <summary>
    /// 开始监听凭据弹窗。每次连接尝试调用一次。
    /// </summary>
    /// <param name="force">
    /// 忽略「自动登录已启用」这个前提。用于「保存并验证」——那时开关可能还没打开，
    /// 但同样需要自动填充。
    /// </param>
    public void StartWatching(bool force = false)
    {
        if (_disposed || !HasStoredPassword)
        {
            return;
        }

        if (!force && !IsEnabled)
        {
            return;
        }

        // 已经在监听就直接返回：状态轮询每 500ms 调一次，重启会不断把重试次数清零。
        if (_watchCancellation is not null)
        {
            return;
        }

        _lastFilledDialog = IntPtr.Zero;

        var cancellation = new CancellationTokenSource();
        _watchCancellation = cancellation;
        _ = Task.Run(() => WatchAsync(cancellation.Token));
    }

    /// <summary>
    /// 停止监听。连接结束或失败时调用。
    /// </summary>
    public void StopWatching()
    {
        var cancellation = _watchCancellation;
        _watchCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatching();
    }

    private async Task WatchAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + WatchDuration;
        var attempts = 0;

        AppLog.Info("自动登录：开始监听 Windows 凭据弹窗。");

        while (!token.IsCancellationRequested
               && attempts < MaxFillAttempts
               && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!TryFillCredentialDialog())
            {
                continue;
            }

            attempts++;
            AppLog.Info($"自动登录：已填写凭据弹窗（第 {attempts} 次，上限 {MaxFillAttempts}）。");

            if (attempts >= MaxFillAttempts)
            {
                AppLog.Warn("自动登录：已达到本次连接的重试上限，停止自动填写。");
            }
        }
    }

    /// <summary>
    /// 找到凭据弹窗就填一次。没有弹窗或填不进去返回 false。
    /// </summary>
    private bool TryFillCredentialDialog()
    {
        var handle = FindCredentialDialog();
        if (handle == IntPtr.Zero || handle == _lastFilledDialog)
        {
            return false;
        }

        var password = ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            var dialog = AutomationElement.FromHandle(handle);
            if (dialog is null)
            {
                return false;
            }

            var edits = dialog.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            AutomationElement? passwordField = null;
            AutomationElement? userNameField = null;

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    if (!edit.Current.IsEnabled)
                    {
                        continue;
                    }

                    if (edit.Current.IsPassword)
                    {
                        passwordField ??= edit;
                    }
                    else
                    {
                        userNameField ??= edit;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // 界面在遍历过程中重建了，这一轮直接放弃，下次轮询再来。
                    return false;
                }
            }

            if (passwordField is null)
            {
                return false;
            }

            // 用户名：只在字段存在且为空时填。用户自己输过的内容不覆盖。
            if (userNameField is not null)
            {
                TrySetValue(userNameField, UserName, onlyWhenEmpty: true);
            }

            if (!TrySetValue(passwordField, password, onlyWhenEmpty: false))
            {
                return false;
            }

            if (!TryInvokeConfirm(dialog))
            {
                AppLog.Warn("自动登录：已填入密码，但没找到可点击的确认按钮。");
                return false;
            }

            _lastFilledDialog = handle;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or NotSupportedException
                                              or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("自动登录：填写凭据弹窗失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 找当前存在的凭据弹窗。
    ///
    /// 现代系统上是 XAML 版（<c>Credential Dialog Xaml Host</c>），
    /// 但 <c>CredUIPromptForWindowsCredentials</c> 在部分配置下走的是经典 Win32 对话框
    /// （<c>#32770</c>）。两种都要认，否则「保存并验证」在某些机器上会填不进去。
    /// </summary>
    private static IntPtr FindCredentialDialog()
    {
        var handle = WindowNativeMethods.FindWindowByClassName(CredentialDialogClassName);
        if (handle != IntPtr.Zero)
        {
            return handle;
        }

        foreach (var keyword in LegacyDialogTitleKeywords)
        {
            handle = WindowNativeMethods.FindWindowByClassName(LegacyDialogClassName, keyword);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static bool TrySetValue(AutomationElement element, string value, bool onlyWhenEmpty)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                || pattern is not ValuePattern valuePattern)
            {
                return false;
            }

            if (valuePattern.Current.IsReadOnly)
            {
                return false;
            }

            if (onlyWhenEmpty && !string.IsNullOrWhiteSpace(valuePattern.Current.Value))
            {
                return true;
            }

            valuePattern.SetValue(value);
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException
                                              or ElementNotEnabledException)
        {
            return false;
        }
    }

    private static bool TryInvokeConfirm(AutomationElement dialog)
    {
        var buttons = dialog.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        // 按钮文案随系统语言变化，同时按 AutomationId 和常见名称匹配，命中一个即可。
        string[] preferredNames = ["确定", "OK", "继续", "Continue", "登录", "Sign in", "提交", "Submit"];

        AutomationElement? fallback = null;
        foreach (AutomationElement button in buttons)
        {
            try
            {
                if (!button.Current.IsEnabled)
                {
                    continue;
                }

                var automationId = button.Current.AutomationId;
                if (string.Equals(automationId, "OkButton", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = button;
                    break;
                }

                var name = button.Current.Name;
                if (Array.Exists(preferredNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                {
                    fallback = button;
                    break;
                }

                // 凭据框通常只有一个按钮，名称是本地化文本时按这个兜底。
                fallback ??= button;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        if (fallback is null)
        {
            return false;
        }

        try
        {
            if (!fallback.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                || pattern is not InvokePattern invokePattern)
            {
                return false;
            }

            invokePattern.Invoke();
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException
                                              or ElementNotEnabledException)
        {
            return false;
        }
    }

    private string? ReadPassword()
    {
        var protectedValue = _settings.Current.Automation.AutoLoginPassword;
        return string.IsNullOrWhiteSpace(protectedValue)
            ? null
            : DpapiProtector.Unprotect(protectedValue);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AutoLoginService));
        }
    }
}

/// <summary>
/// 用于 RDP 静默登录的凭据。
/// </summary>
/// <param name="UserName">用户名。</param>
/// <param name="Domain">域，本地账户为机器名。</param>
/// <param name="Password">明文密码，仅存在于内存中。</param>
internal readonly record struct RdpCredential(string UserName, string Domain, string Password);
