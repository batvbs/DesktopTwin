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
using DesktopTwin.Localization;

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

    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string userName,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum NetJoinStatus
    {
        Unknown = 0,
        Unjoined = 1,
        Workgroup = 2,
        Domain = 3
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(
        string? server,
        out IntPtr nameBuffer,
        out NetJoinStatus status);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>
    /// 一次连接最多自动填几次。凭据框反复出现通常意味着密码本身就是错的，
    /// 继续填只会把账户往锁定策略上推。
    /// </summary>
    private const int MaxFillAttempts = 1;

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
    /// 是否已经存了「当前账户」的密码（且能解开）。
    ///
    /// 多账户：本机可能用不同账户登录桌面分身，每个账户的密码单独存一份，
    /// 这里只回答「当前这次连接要用的那个账户，有没有存过密码」。
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
    /// 当前账户在密码表里的键。用户名不区分大小写。
    /// </summary>
    public string AccountKey => UserName.ToLowerInvariant();

    /// <summary>
    /// 自动登录使用的域。本地账户就是机器名。
    /// </summary>
    public string Domain => Environment.UserDomainName;

    /// <summary>
    /// 已经存过密码的账户列表（用于设置界面展示，不含任何密码信息）。
    /// </summary>
    public IReadOnlyList<string> StoredAccountNames
    {
        get
        {
            var passwords = _settings.Current.Automation.AutoLoginPasswords;
            var names = new List<string>(passwords.Count);
            foreach (var pair in passwords)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    names.Add(pair.Key);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    /// <summary>
    /// 指定账户是否存过密码。
    /// </summary>
    /// <param name="userName">账户名（不区分大小写）。</param>
    public bool HasStoredPasswordFor(string userName)
    {
        return FindAccountKey(userName) is not null;
    }

    /// <summary>
    /// 保存密码（DPAPI 加密后写进设置）。只影响当前账户，不动其它账户的记录。
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

        var passwords = _settings.Current.Automation.AutoLoginPasswords;

        // 同一个账户可能以不同大小写存在过（账户名改过大小写、或从旧文件读回来），
        // 先删掉忽略大小写等价的那一条，避免同一个账户堆出两份密码。
        var existing = FindAccountKey(AccountKey);
        if (existing is not null)
        {
            passwords.Remove(existing);
        }

        passwords[UserName] = protectedValue;
        _settings.SaveSoon();
        return true;
    }

    /// <summary>
    /// 清除「当前账户」已保存的密码；其它账户的记录保留。
    /// </summary>
    public void ClearPassword()
    {
        ThrowIfDisposed();
        var existing = FindAccountKey(AccountKey);
        if (existing is not null && _settings.Current.Automation.AutoLoginPasswords.Remove(existing))
        {
            _settings.SaveSoon();
        }
    }

    /// <summary>
    /// 在密码表里找与给定账户名忽略大小写相等的键。
    /// </summary>
    private string? FindAccountKey(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        foreach (var key in _settings.Current.Automation.AutoLoginPasswords.Keys)
        {
            if (string.Equals(key, userName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
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

        /// <summary>账户已被锁定（连续失败次数过多）。</summary>
        AccountLocked,

        /// <summary>必须先修改密码才能登录。</summary>
        PasswordMustChange,

        /// <summary>无法判定（权限不足或未知错误）。</summary>
        Unknown
    }

    /// <summary>
    /// 校验一组凭据当前是否有效。
    ///
    /// 用 <c>LogonUser</c> 做一次**交互式**登录验证（LOGON32_LOGON_INTERACTIVE）：
    /// 不产生界面、不创建进程、不加载用户配置文件。
    ///
    /// 为什么不用网络登录（NETWORK）：那只比对 LSA 里的本地验证器。微软账户在云端改密后，
    /// 本机缓存仍是旧密码，网络登录会对新密码返回 1326——看起来像密码错了。
    /// 交互式登录会先比对缓存、不匹配则**联网校验并回写缓存**，实测改密后走这条路
    /// 既能得到真实结论，顺手把缓存也修好了。
    /// </summary>
    /// <param name="password">待校验的密码。</param>
    /// <param name="errorCode">Win32 错误码，成功时为 0。</param>
    public PasswordCheckResult VerifyPassword(string password, out int errorCode)
    {
        var (userName, domain) = ResolveLogonIdentity();
        return VerifyPassword(userName, domain, password, out errorCode);
    }

    /// <summary>
    /// 用指定的用户名/域校验凭据。
    /// </summary>
    /// <param name="userName">用户名。</param>
    /// <param name="domain">域；本地账户留空即可，微软账户用 MicrosoftAccount。</param>
    /// <param name="password">密码。</param>
    /// <param name="errorCode">Win32 错误码，成功时为 0。</param>
    public PasswordCheckResult VerifyPassword(
        string userName,
        string? domain,
        string password,
        out int errorCode)
    {
        errorCode = 0;

        if (LogonUserW(
                userName,
                domain,
                password,
                Logon32LogonInteractive,
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
            1909 => PasswordCheckResult.AccountLocked,
            1907 => PasswordCheckResult.PasswordMustChange,
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
    ///
    /// 多账户时取第一个候选：自动登录本来就只有一次机会，猜错了也只是这次失败、
    /// 用户手动输一遍，不值得为了「可能猜错」而整个放弃。
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
    /// 这个名称是不是本机账户存储里登记过的微软账户。
    ///
    /// 用来区分「微软账户邮箱」和「AD 域 UPN」：前者要配 MicrosoftAccount 域，
    /// 后者按 Windows 的规定必须传 null 域。
    /// </summary>
    /// <summary>
    /// 这台机器是否加入了 Entra（Azure AD）。
    ///
    /// 加入 Entra 的机器在 NetGetJoinInformation 里仍然报「工作组」，只看域加入状态
    /// 会把工作账户的 UPN 当成微软账户，套上 MicrosoftAccount 域之后自动登录必然失败。
    /// 系统把加入信息放在 CloudDomainJoin 下，键存在即为已加入。
    /// </summary>
    private static bool IsEntraJoined()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");

            return key is not null && key.GetSubKeyNames().Length > 0;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            // 查不到按「已加入」处理：宁可让用户显式写 MicrosoftAccount\邮箱，
            // 也不要把工作账户套成微软账户。
            AppLog.Warn("查询 Entra 加入状态失败，按已加入处理。", exception);
            return true;
        }
    }

    /// <summary>
    /// 这台机器是否加入了 AD 域。没入域时不存在域账户，UPN 只能是微软账户。
    ///
    /// 用 NetGetJoinInformation 而不是「主 DNS 后缀是否非空」：工作组机器也可以设置
    /// 主 DNS 后缀，那种机器会被误判成已入域，微软账户就再也匹配不上了。
    /// </summary>
    private static bool IsDomainJoined()
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (NetGetJoinInformation(null, out buffer, out var status) != 0)
            {
                // 查不到就按「已入域」处理：宁可让用户显式写 MicrosoftAccount\邮箱，
                // 也不要把 AD 账户硬套成微软账户。
                AppLog.Warn("查询域加入状态失败，按已入域处理。");
                return true;
            }

            return status == NetJoinStatus.Domain;
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or MarshalDirectiveException)
        {
            AppLog.Warn("查询域加入状态失败，按已入域处理。", exception);
            return true;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try
                {
                    NetApiBufferFree(buffer);
                }
                catch (DllNotFoundException)
                {
                }
            }
        }
    }

    private static bool IsKnownMicrosoftAccount(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MicrosoftAccountPropertiesPath);
            if (key is null)
            {
                return false;
            }

            foreach (var candidate in key.GetSubKeyNames())
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("读取微软账户列表失败。", exception);
        }

        return false;
    }

    /// <summary>
    /// 决定 <c>LogonUser</c> 用哪个用户名/域：微软账户用 UPN + MicrosoftAccount，本地账户用机器名。
    /// </summary>
    private (string UserName, string? Domain) ResolveLogonIdentity()
    {
        var raw = _settings.Current.Automation.AutoLoginUserName;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var configured = raw.Trim();

            // 先拆「域\用户」再判 @：MicrosoftAccount\alice@example.com 是合法写法，
            // 先看 @ 会把整串当成 UPN 配 null 域，LogonUser 必然失败。
            var separator = configured.IndexOf('\\');
            if (separator > 0 && separator < configured.Length - 1)
            {
                return (configured[(separator + 1)..], configured[..separator]);
            }

            // 只剩 UPN：它就是要用的账户，不必去注册表里猜。
            if (configured.Contains('@'))
            {
                // 「带 @」不等于微软账户：AD 域 UPN（alice@example.org）按 Windows 的规定
                // 必须传 null 域，硬套 MicrosoftAccount 会在校验密码时直接失败。
                if (IsKnownMicrosoftAccount(configured))
                {
                    return (configured, "MicrosoftAccount");
                }

                // 当前用户的 hive 里没有它，也不代表就不是微软账户——别的 Windows 用户
                // 的账户不会出现在这个进程的 CurrentUser 下。这里补一条环境判据：
                // 既没加入 AD 域、也没加入 Entra 的机器上不存在「工作账户」，
                // UPN 只可能是微软账户；两类加入都算「有组织账户」，
                // 一律保持 null 域交给系统解析，否则工作账户会被套上 MicrosoftAccount 而失败。
                // 要在这类机器上用微软账户，写 MicrosoftAccount\邮箱 即可（上面已处理）。
                return IsDomainJoined() || IsEntraJoined()
                    ? (configured, null)
                    : (configured, "MicrosoftAccount");
            }

            // 显式写了裸账户名：
            // 与当前登录用户同名时，用当前用户的域——域成员机器上裸写账户名通常就是它自己，
            // 传机器名会让域账户校验失败。其余情况仍按本机账户解析。
            if (string.Equals(configured, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                return (configured, Environment.UserDomainName);
            }

            return (configured, Environment.MachineName);
        }

        // 未配置时才跟随当前登录身份：微软账户需要换成邮箱，本地账户用当前域/机器名。
        if (TryGetMicrosoftAccountEmail(out var email))
        {
            return (email, "MicrosoftAccount");
        }

        // 域成员机器上 UserDomainName 是域、本地账户时是机器名，
        // 直接用它才能和系统里的登录身份对上（写死机器名会让域账户验证失败）。
        return (Environment.UserName, Environment.UserDomainName);
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

        // 用与校验完全相同的身份：之前凭据走 Domain 属性（Environment.UserDomainName），
        // 校验走的却是机器名或 MicrosoftAccount，域成员机器上会验过却连不上。
        var (userName, domain) = ResolveLogonIdentity();
        credential = new RdpCredential(userName, domain ?? string.Empty, password);
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
            error = Locale.Get("AutoLogin.System.NotFound");
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
                error = Locale.Get("AutoLogin.System.NotStarted");
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
            error = Locale.Format("AutoLogin.System.OpenError", exception.GetBaseException().Message);
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

            // 用解析出来的登录身份填：微软账户要在凭据框里写邮箱，本地账户写用户名。
            var (resolvedUserName, resolvedDomain) = ResolveLogonIdentity();

            // 显式配了「域\用户」时，框里也要带域：同名不同域是两个账户，
            // 只填后半段可能登到别人头上。微软账户例外——框里要的是邮箱。
            var dialogUserName = resolvedUserName;
            if (!string.IsNullOrEmpty(resolvedDomain)
                && !string.Equals(resolvedDomain, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase))
            {
                dialogUserName = resolvedDomain + "\\" + resolvedUserName;
            }

            // 用户名只在字段为空时填，用户自己输过的不覆盖。
            //
            // 填错也不要紧：自动登录只有一次机会（MaxFillAttempts = 1），
            // 这次没成就交给用户手动输入，不会反复试。
            if (userNameField is not null)
            {
                TrySetValue(userNameField, dialogUserName, onlyWhenEmpty: true);
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

    /// <summary>
    /// 点凭据框里的确认按钮。
    ///
    /// 按钮文案随系统语言变化，不能取「第一个可用按钮」兜底：遇到不认识的语言、
    /// 框里又有「取消」时，取第一个等于替用户点了取消，而调用方还会把它记成
    /// 「已经填过」，这一轮就再也不会重试。
    /// </summary>
    private static bool TryInvokeConfirm(AutomationElement dialog)
    {
        var buttons = dialog.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        // 按钮文案随系统语言变化，同时按 AutomationId 和常见名称匹配，命中一个即可。
        // 除中英文外补上繁体、韩、俄、西四语的确认字样：这些语言下原来一个都匹配不上，
        // 而 RDP 凭据框固定有「确认 / 取消」两个按钮，用「只剩一个按钮」兜底也救不回来，
        // 结果是密码填了却没提交，看起来像自动登录没生效。
        string[] preferredNames =
        [
            "确定", "確定", "확인", "ОК",
            "OK", "Aceptar",
            "继续", "Continue",
            "登录", "Sign in",
            "提交", "Submit"
        ];

        // 取消除外的字样：确认按钮被禁用时，取消会是「唯一可用的按钮」，
        // 单按钮兜底就会把它点下去——调用方还会记成「已经填过」，这一轮不再重试。
        string[] cancelNames = ["取消", "取消(&C)", "Cancel", "キャンセル", "취소", "Отмена", "Cancelar"];

        AutomationElement? matched = null;
        var otherEnabled = new List<AutomationElement>();

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
                    matched = button;
                    break;
                }

                var name = button.Current.Name;
                if (Array.Exists(preferredNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = button;
                    break;
                }

                if (Array.Exists(cancelNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                {
                    // 取消类按钮永远不参与兜底。
                    continue;
                }

                otherEnabled.Add(button);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        // 名称没匹配上时，只有「整个对话框就这一个可用按钮」才敢点：
        // 系统语言不认识、又同时有「取消」时，取第一个等于替用户点了取消，
        // 而调用方还会把它记成「已经填过」，这一轮就再也不会重试。
        var target = matched ?? (otherEnabled.Count == 1 ? otherEnabled[0] : null);
        if (target is null)
        {
            return false;
        }

        try
        {
            if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
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
        var passwords = _settings.Current.Automation.AutoLoginPasswords;
        var key = FindAccountKey(AccountKey);
        if (key is null || !passwords.TryGetValue(key, out var protectedValue)
            || string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        return DpapiProtector.Unprotect(protectedValue);
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
