using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopTwin.Models;
using DesktopTwin.Services;
using DrawingSize = System.Drawing.Size;
using DesktopTwin.Localization;

namespace DesktopTwin.Interop;

internal sealed class RdpActiveXHost : AxHost
{
    // Windows 10+ 自带的非脚本化 RDP ActiveX 控件（MsRdpClient10）。
    private const string RdpClientClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private const short VariantFalse = 0;
    private const short VariantTrue = -1;
    private const int RedirectAudioToClient = 0;
    private const int DisableRemoteAudio = 2;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private static readonly RemoteKey LeftWindowsKey = new(0x5B, IsExtended: true);
    private static readonly RemoteKey DKey = new(0x20, IsExtended: false);
    private static readonly RemoteKey TabKey = new(0x0F, IsExtended: false);
    private ConnectionPointCookie? _eventCookie;
    private RdpEventSink? _eventSink;
    private bool _connectionAttemptInProgress;
    private bool _connectionFailureReported;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionDiagnostic;
    private bool _disconnectRequested;
    private bool _sendSystemShortcutsToRemote = true;
    private bool _audioMuted;

    /// <summary>
    /// 待重连的代际号。取消（DisconnectSession / ResetDisconnectState）时自增，
    /// 让已经排上、还在等「会话释放」的那次重连作废——否则它醒来后照样去连，
    /// 把刚刚发起的断开又接回来。
    /// </summary>
    private int _reconnectGeneration;
    private DrawingSize? _pendingReconnectDesktopSize;
    private bool _smartSizingEnabled = true;
    private bool _inputBlocked;
    private Func<(string UserName, string Domain, string Password)?>? _credentialProvider;

    internal event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    internal event EventHandler? LoginCompleted;

    internal ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic =>
        _lastConnectionDiagnostic;

    internal RdpActiveXHost()
        : base(RdpClientClsid)
    {
        Dock = DockStyle.Fill;
    }

    internal int ConnectedState
    {
        get
        {
            if (!IsHandleCreated)
            {
                return 0;
            }

            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "Connected"),
                CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 设置自动登录凭据的来源。每次发起连接时都会调用一次，取不到就退回交互式输入。
    ///
    /// 用回调而不是缓存值：设置类重连（改分辨率、切音频）也会走连接流程，
    /// 回调能保证这些路径同样拿得到最新凭据。
    /// </summary>
    internal void SetCredentialProvider(
        Func<(string UserName, string Domain, string Password)?>? provider)
    {
        _credentialProvider = provider;
    }

    internal void ConnectToChildSession(DrawingSize desktopSize)
    {        if (ConnectedState != 0)
        {
            return;
        }

        ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        var client = GetRequiredOcx();
        var width = Math.Clamp(desktopSize.Width, 200, 8192);
        var height = Math.Clamp(desktopSize.Height, 200, 8192);

        // 显式使用回环 IP 而不是 "localhost"：目标地址不参与任何名称解析，
        // 避免 hosts 文件或 DNS 配置把连接引到别处。
        SetComProperty(client, "Server", "127.0.0.1");
        SetComProperty(client, "DesktopWidth", width);
        SetComProperty(client, "DesktopHeight", height);
        SetComProperty(client, "ColorDepth", 32);
        SetComProperty(client, "ConnectingText", Locale.Get("Rdp.ConnectingText"));
        SetComProperty(client, "DisconnectedText", Locale.Get("Rdp.DisconnectedText"));

        var securedSettings = GetComProperty(client, "SecuredSettings2")
            ?? throw new COMException(Locale.Get("Rdp.Error.SecuredSettings"));
        RunComStep(Locale.Get("Rdp.Step.ShortcutTarget"), () =>
        {
            var hookMode = _sendSystemShortcutsToRemote ? 1 : 0;
            SetComProperty(securedSettings, "KeyboardHookMode", hookMode);

            // 读回来确认：这个属性决定 RDP 是否装键盘钩子。
            // 装了就表示组合键会被转发进远程会话，本地全局热键随之失效——
            // 用户报「在分身里按热键没反应」时，这一行是第一个要看的地方。
            try
            {
                var actual = GetComProperty(securedSettings, "KeyboardHookMode");
                AppLog.Info($"KeyboardHookMode 已写入 {hookMode}，读回 {actual ?? "(空)"}。");
            }
            catch (Exception exception) when (exception is COMException
                                                  or MissingMemberException
                                                  or InvalidCastException)
            {
                AppLog.Warn("KeyboardHookMode 读回失败，无法确认是否生效。", exception);
            }
        });
        RunComStep(Locale.Get("Rdp.Step.AudioRedirection"), () =>
            SetComProperty(
                securedSettings,
                "AudioRedirectionMode",
                _audioMuted ? DisableRemoteAudio : RedirectAudioToClient));

        var advancedSettings = GetComProperty(client, "AdvancedSettings7")
            ?? throw new COMException(Locale.Get("Rdp.Error.AdvancedSettings"));
        RunComStep(Locale.Get("Rdp.Step.Port"), () =>
            SetComProperty(
                advancedSettings,
                "RDPPort",
                ChildSessionNativeMethods.GetConfiguredRdpPort()));
        RunComStep(Locale.Get("Rdp.Step.CredSsp"), () =>
            SetComProperty(advancedSettings, "EnableCredSspSupport", true));
        RunComStep(Locale.Get("Rdp.Step.RemoteWindowsKey"), () =>
            SetComProperty(advancedSettings, "EnableWindowsKey", 1));
        RunComStep(Locale.Get("Rdp.Step.DisplayScale"), () =>
            SetComProperty(advancedSettings, "SmartSizing", _smartSizingEnabled));

        object connectToChildSession = true;
        RunComStep(Locale.Get("Rdp.Step.ChildSession"), () =>
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            TrySetExtendedProperty(extendedSettings, "EnableZoom", true);
            extendedSettings.set_Property("ConnectToChildSession", ref connectToChildSession);
        });

        ApplyCredentials(client);

        _connectionAttemptInProgress = true;
        _connectionFailureReported = false;
        _lastConnectionDiagnostic = null;
        _disconnectRequested = false;
        try
        {
            RunComStep(Locale.Get("Rdp.Step.Connect"), () => InvokeComMethod(client, "Connect"));
        }
        catch
        {
            _connectionAttemptInProgress = false;
            throw;
        }
    }

    internal void SendShowDesktopShortcut()
    {
        KeyStroke[] strokes =
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ];

        SendShortcut(strokes, "Win+D");
    }

    internal void SendTaskViewShortcut()
    {
        KeyStroke[] strokes =
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ];

        SendShortcut(strokes, "Win+Tab");
    }

    internal void SetSmartSizing(bool enabled)
    {
        _smartSizingEnabled = enabled;
        if (!IsHandleCreated)
        {
            return;
        }

        var advancedSettings = GetComProperty(GetRequiredOcx(), "AdvancedSettings7")
            ?? throw new COMException(Locale.Get("Rdp.Error.AdvancedSettings"));
        RunComStep(Locale.Get("Rdp.Step.DisplayScale"), () =>
            SetComProperty(advancedSettings, "SmartSizing", enabled));

        // 这个属性允许联机修改，但控件是在「尺寸变化」时按它重算缩放比例的：
        // 只改属性不会重绘，画面上看不出任何变化（全屏自适应/1:1 切换失灵的原因）。
        // 把尺寸推一像素再复位，逼它按新设置重新排版。
        if (ConnectedState != 1 || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            // 尺寸要在回调里读：全屏是「先定缩放模式、再改窗口尺寸」，
            // 排队期间窗口可能刚变成全屏大小，用外面的旧值会把控件按回旧尺寸。
            if (IsDisposed || Disposing || Width <= 1 || Height <= 1)
            {
                return;
            }

            var size = Size;
            Size = new DrawingSize(size.Width - 1, size.Height);
            Size = size;
        }));
    }

    internal void SetSendSystemShortcutsToRemote(bool enabled)
    {
        _sendSystemShortcutsToRemote = enabled;
    }

    internal void SetAudioMuted(bool muted)
    {
        _audioMuted = muted;
    }

    /// <summary>
    /// 屏蔽或恢复本机进入桌面分身的鼠标键盘输入。期望状态会被记住，
    /// 每次连接登录后由 ChildSessionService 重新施加。
    /// </summary>
    internal void SetInputBlocked(bool blocked)
    {
        _inputBlocked = blocked;
        if (!IsHandleCreated)
        {
            return;
        }

        ChildSessionNativeMethods.SetRdpInputWindowEnabled(Handle, !blocked);
    }

    /// <summary>
    /// 清掉「正在断开」的排队状态。
    ///
    /// <see cref="Disconnect"/> 会置 <c>_disconnectRequested</c>，要等 <c>OnDisconnected</c>
    /// 回调回来才复位。回调一旦丢失，就会永远卡在「正在断开」：
    /// <see cref="ReconnectToChildSession"/> 走的是「只排队、不重连」那条分支，
    /// 表现为切回分身时画面一直黑着，用户只能自己去点「启动并连接」。
    ///
    /// 调用方必须已经确认过会话状态，否则可能打断一次真正的断开。
    /// </summary>
    internal void ResetDisconnectState()
    {
        _reconnectGeneration++;
        _disconnectRequested = false;
        _pendingReconnectDesktopSize = null;
    }

    internal void ReconnectToChildSession(DrawingSize desktopSize)
    {
        if (_disconnectRequested || _pendingReconnectDesktopSize.HasValue)
        {
            _pendingReconnectDesktopSize = desktopSize;
            return;
        }

        if (ConnectedState == 0)
        {
            // 直接连也作废在途的那次「等会话释放」重连：它拿的是旧尺寸，
            // 等这边失败（控件回 0）时它会醒过来，用旧分辨率把新的一次顶掉。
            _reconnectGeneration++;
            ConnectToChildSession(desktopSize);
            return;
        }

        _pendingReconnectDesktopSize = desktopSize;
        try
        {
            if (!DisconnectCore())
            {
                _pendingReconnectDesktopSize = null;
                ConnectToChildSession(desktopSize);
            }
        }
        catch
        {
            _pendingReconnectDesktopSize = null;
            throw;
        }
    }

    /// <summary>
    /// 作废尚未执行的重连。
    ///
    /// 切到主桌面时用：那次重连可能正停在「等会话释放」上，等完就会把分身连回来，
    /// 和「切到整台电脑」的目的正好相反。只是断开（Disconnect）不会动这个标记，
    /// 所以要显式取消。
    /// </summary>
    internal void CancelPendingReconnect()
    {
        _reconnectGeneration++;
        _pendingReconnectDesktopSize = null;
    }

    internal void DisconnectSession()
    {
        _reconnectGeneration++;
        _pendingReconnectDesktopSize = null;
        _ = DisconnectCore();
    }

    /// <summary>
    /// 主动断开 RDP 连接，但保留远端会话（桌面上的程序继续运行）。
    /// 走这条路径不会触发「连接意外断开」的失败提示。
    /// </summary>
    /// <returns>确实发起了断开返回 true。</returns>
    internal bool Disconnect() => DisconnectCore();

    private bool DisconnectCore()
    {
        if (IsHandleCreated)
        {
            ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        }

        if (ConnectedState == 0)
        {
            return false;
        }

        _disconnectRequested = true;
        try
        {
            InvokeComMethod(GetRequiredOcx(), "Disconnect");
            return true;
        }
        catch
        {
            _disconnectRequested = false;
            throw;
        }
    }

    internal bool IsInputWindowFocused()
    {
        return ConnectedState == 1
               && ChildSessionNativeMethods.IsRdpInputWindowFocused(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (IsHandleCreated)
        {
            ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        }

        base.OnHandleDestroyed(e);
    }

    protected override void CreateSink()
    {
        base.CreateSink();

        _eventSink = new RdpEventSink(this);
        var ocx = GetOcx()
                  ?? throw new InvalidOperationException(
                      Locale.Get("Rdp.Error.SinkNotReady"));
        _eventCookie = new ConnectionPointCookie(
            ocx,
            _eventSink,
            typeof(IMsTscAxEvents));
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
            _eventCookie = null;
            _eventSink = null;
        }
        finally
        {
            base.DetachSink();
        }
    }

    private void SendShortcut(KeyStroke[] strokes, string displayName)
    {
        if (ConnectedState != 1)
        {
            throw new InvalidOperationException(
                Locale.Format("Rdp.Error.CannotSend", displayName));
        }

        // 组合键由调用方显式发起，发送期间短暂恢复输入窗口，发完立刻回到屏蔽状态。
        if (_inputBlocked)
        {
            ChildSessionNativeMethods.SetRdpInputWindowEnabled(Handle, true);
        }

        try
        {
            if (!ChildSessionNativeMethods.TryFocusRdpInputWindow(Handle))
            {
                throw new InvalidOperationException(Locale.Get("Rdp.Error.CannotFocus"));
            }

            RunComStep($"向 Child Session 发送 {displayName}", () => SendKeyStrokes(strokes));
        }
        finally
        {
            if (_inputBlocked)
            {
                ChildSessionNativeMethods.SetRdpInputWindowEnabled(Handle, false);
            }
        }
    }

    private void SendKeyStrokes(KeyStroke[] strokes)
    {
        var keyUpStates = new short[strokes.Length];
        var keyData = new int[strokes.Length];

        for (var index = 0; index < strokes.Length; index++)
        {
            var stroke = strokes[index];
            keyUpStates[index] = stroke.IsKeyUp ? VariantTrue : VariantFalse;
            keyData[index] = CreateRdpScanCode(stroke.Key);
        }

        var nonScriptableClient = (IMsRdpClientNonScriptable)GetRequiredOcx();
        nonScriptableClient.SendKeys(
            strokes.Length,
            ref keyUpStates[0],
            ref keyData[0]);
    }

    private static int CreateRdpScanCode(RemoteKey key)
    {
        const int extendedScanCodeFlag = 0x0100;
        return key.ScanCode | (key.IsExtended ? extendedScanCodeFlag : 0);
    }

    private void OnLoginComplete()
    {
        _connectionAttemptInProgress = false;
        _connectionFailureReported = false;
        _lastConnectionDiagnostic = null;
        LoginCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(int disconnectReason)
    {
        var failedWhileConnecting = _connectionAttemptInProgress;
        _connectionAttemptInProgress = false;

        if (_disconnectRequested)
        {
            _disconnectRequested = false;
            if (_pendingReconnectDesktopSize.HasValue && !IsDisposed && !Disposing)
            {
                try
                {
                    BeginInvoke(new Action(ConnectPendingReconnect));
                }
                catch (InvalidOperationException)
                {
                    _pendingReconnectDesktopSize = null;
                }
            }
            return;
        }

        var extendedDisconnectReason = TryGetExtendedDisconnectReason();
        if (_connectionFailureReported
            || (!failedWhileConnecting
                && IsNormalDisconnect(disconnectReason, extendedDisconnectReason)))
        {
            return;
        }

        var errorDescription = TryGetErrorDescription(
            disconnectReason,
            extendedDisconnectReason);
        var failureTitle = failedWhileConnecting
            ? "RDP 连接失败"
            : "RDP 连接意外断开";
        var message = string.IsNullOrWhiteSpace(errorDescription)
            ? $"{failureTitle}。\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}"
            : $"{failureTitle}：{errorDescription}\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}";

        ReportConnectionFailure(
            message,
            disconnectReason,
            extendedDisconnectReason);
    }

    private async void ConnectPendingReconnect()
    {
        var generation = _reconnectGeneration;

        await Task.Delay(ReconnectDelay);

        // 等待期间可能被取消（主动断开）或被新的一次重连取代：这时不能再连。
        if (generation != _reconnectGeneration || IsDisposed || Disposing)
        {
            return;
        }

        var desktopSize = _pendingReconnectDesktopSize;
        _pendingReconnectDesktopSize = null;
        if (!desktopSize.HasValue || IsDisposed || Disposing)
        {
            return;
        }

        // 控件回到「已断开」不代表上一轮连接在系统里已经收尾。这时立刻重连，
        // RDS 会用「访问被拒绝」把新连接顶回来——日志里每次改设置都这样，
        // 隔几秒再连就好。等会话真的不再是 Active 再连，和「切回分身」那条
        // 路径（先等会话离开 Active 再 StartAsync）保持一致。
        await WaitForChildSessionReleasedAsync(TimeSpan.FromSeconds(10));
        if (generation != _reconnectGeneration || IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            ConnectToChildSession(desktopSize.Value);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            ReportConnectionFailure(
                $"RDP 自动重新连接失败：{actualException.Message}",
                errorCode);
        }
    }

    /// <summary>
    /// 等分身会话离开 Active 状态。没有会话（已经注销）也算可以连了。
    /// </summary>
    private static async Task WaitForChildSessionReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var sessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            if (sessionId is null || !ChildSessionNativeMethods.IsSessionActive(sessionId.Value))
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private void OnFatalError(int errorCode)
    {
        _connectionAttemptInProgress = false;
        if (_disconnectRequested)
        {
            return;
        }

        ReportConnectionFailure(
            $"RDP 客户端发生致命错误：{GetFatalErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}",
            errorCode);
    }

    private void OnLogonError(int errorCode)
    {
        if (_disconnectRequested)
        {
            return;
        }

        var message =
            $"RDP 登录阶段：{GetLogonErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}";
        if (IsNonTerminalLogonEvent(errorCode))
        {
            _lastConnectionDiagnostic =
                new ChildSessionConnectionFailedEventArgs(message, errorCode);
            return;
        }

        _connectionAttemptInProgress = false;
        ReportConnectionFailure(
            message.Replace("RDP 登录阶段：", "RDP 登录失败：", StringComparison.Ordinal),
            errorCode);
    }

    private int TryGetExtendedDisconnectReason()
    {
        try
        {
            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "ExtendedDisconnectReason"),
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return 0;
        }
    }

    private string? TryGetErrorDescription(
        int disconnectReason,
        int extendedDisconnectReason)
    {
        try
        {
            return Convert.ToString(
                InvokeComMethod(
                    GetRequiredOcx(),
                    "GetErrorDescription",
                    disconnectReason,
                    extendedDisconnectReason),
                CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    private void ReportConnectionFailure(
        string message,
        int errorCode,
        int? extendedErrorCode = null)
    {
        if (_connectionFailureReported)
        {
            return;
        }

        _connectionFailureReported = true;
        ConnectionFailed?.Invoke(
            this,
            new ChildSessionConnectionFailedEventArgs(
                message,
                errorCode,
                extendedErrorCode));
    }

    private static bool IsNormalDisconnect(
        int disconnectReason,
        int extendedDisconnectReason)
    {
        // 1～3 分别表示本地主动断开、远端用户断开和服务器主动断开，并非连接错误。
        // 扩展原因 1、2 分别表示 API 主动断开和 API 主动注销。
        return disconnectReason is 1 or 2 or 3
               && extendedDisconnectReason is 0 or 1 or 2;
    }

    private static string FormatErrorCode(int errorCode)
    {
        return $"{errorCode} (0x{unchecked((uint)errorCode):X8})";
    }

    private static string GetFatalErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            0 => "发生未知错误。",
            1 => "发生内部错误（1）。",
            2 => "内存不足。",
            3 => "无法创建 RDP 窗口。",
            4 => "发生内部错误（2）。",
            5 => "RDP 客户端进入了无效状态。",
            6 => "发生内部错误（4）。",
            7 => "建立客户端连接时发生不可恢复的错误。",
            100 => "Windows 套接字初始化失败。",
            _ => "发生未识别的致命错误。"
        };
    }

    private static string GetLogonErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            -7 => "Winlogon 正在显示“拒绝断开现有会话”对话框。",
            -6 => "Winlogon 正在显示“无权限”对话框。",
            -5 => "Winlogon 正在显示会话争用选项。",
            -4 => "Winlogon 正在显示重新连接选项。",
            -3 => "Winlogon 已静默终止登录。",
            -1 => "访问被拒绝。",
            0 => "登录凭据无效。",
            1 => "密码已过期，必须先修改密码。",
            2 => "登录或登录后的处理发生错误。",
            3 => "RDP 客户端正在显示登录警告。",
            unchecked((int)0xC000006D) => "用户名或身份验证信息无效。",
            unchecked((int)0xC000006E) => "身份验证受到用户账户限制。",
            unchecked((int)0xC0000224) => "密码已过期，必须先修改密码。",
            _ => "登录阶段发生未识别的错误或事件。"
        };
    }

    private static bool IsNonTerminalLogonEvent(int errorCode)
    {
        // 这些代码表示登录仍在继续或 ActiveX 正在显示可交互选项，不应提前判定连接失败。
        return errorCode is -5 or -4 or -2 or 3;
    }

    private object GetRequiredOcx()
    {
        if (!IsHandleCreated)
        {
            // WindowsFormsHost 在未连接空白态会被折叠，普通 CreateControl() 会因不可见而跳过创建。
            // 读取 Handle 会忽略可见性并强制 AxHost 创建底层窗口及 ActiveX 实例。
            _ = Handle;
        }

        return GetOcx()
               ?? throw new InvalidOperationException(
                   "RDP ActiveX 控件尚未完成初始化，无法访问 COM 实例。");
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);
    }

    private static void TrySetExtendedProperty(
        IMsRdpExtendedSettings extendedSettings,
        string propertyName,
        object value)
    {
        try
        {
            extendedSettings.set_Property(propertyName, ref value);
        }
        catch (COMException)
        {
            // 旧版 MsTscAx 可能不支持该扩展属性，继续使用原有显示行为。
        }
    }

    /// <summary>
    /// 把自动登录凭据交给 RDP 客户端。
    ///
    /// 每一步都单独兜住异常：凭据设不上只是退回「让用户自己输」，
    /// 不该因为一个可选属性不支持就让整次连接失败。
    /// </summary>
    private void ApplyCredentials(object client)
    {
        var credential = _credentialProvider?.Invoke();
        if (credential is not { } value || string.IsNullOrWhiteSpace(value.UserName))
        {
            // 这里以前是静默返回的：连不上时完全看不出是「没提供凭据」还是
            // 「提供了但认证失败」，两种情况的修法完全不同。
            AppLog.Info("自动登录：本次连接没有可用凭据，将走交互式输入。");
            return;
        }

        AppLog.Info(
            $"自动登录：已提供凭据（用户 {value.UserName}，"
            + $"域 {(string.IsNullOrEmpty(value.Domain) ? "空" : value.Domain)}，"
            + $"密码长度 {value.Password?.Length ?? 0}）。");

        try
        {
            SetComProperty(client, "UserName", value.UserName);
        }
        catch (Exception exception) when (IsCredentialStepException(exception))
        {
            AppLog.Warn("设置 RDP 登录用户名失败，将退回手动输入。", exception);
        }

        if (!string.IsNullOrWhiteSpace(value.Domain))
        {
            try
            {
                SetComProperty(client, "Domain", value.Domain);
            }
            catch (Exception exception) when (IsCredentialStepException(exception))
            {
                AppLog.Warn("设置 RDP 登录域失败，将退回手动输入。", exception);
            }
        }

        if (string.IsNullOrEmpty(value.Password))
        {
            return;
        }

        try
        {
            // 密码只经过 RDP 的凭据通道，不写入任何持久化存储。
            ((IMsRdpClientNonScriptable)client).put_ClearTextPassword(value.Password);
        }
        catch (Exception exception) when (IsCredentialStepException(exception))
        {
            AppLog.Warn("设置 RDP 登录密码失败，将退回手动输入。", exception);
        }
    }

    private static bool IsCredentialStepException(Exception exception) =>
        exception.GetBaseException() is COMException
            or InvalidCastException
            or TargetInvocationException
            or MissingMemberException
            or InvalidOperationException;

    private static object? InvokeComMethod(object target, string methodName, params object[]? args)
    {
        return target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);
    }

    private static void RunComStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var actualException = exception.GetBaseException();
            if (actualException is COMException comException)
            {
                throw new COMException(
                    Locale.Format("Rdp.Step.Failed", stepName, comException.Message),
                    comException.ErrorCode);
            }

            throw;
        }
    }

    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    [TypeLibType(TypeLibTypeFlags.FDispatchable)]
    private interface IMsTscAxEvents
    {
        [DispId(1)]
        void OnConnecting();

        [DispId(2)]
        void OnConnected();

        [DispId(3)]
        void OnLoginComplete();

        [DispId(4)]
        void OnDisconnected([In] int disconnectReason);

        [DispId(5)]
        void OnEnterFullScreenMode();

        [DispId(6)]
        void OnLeaveFullScreenMode();

        [DispId(7)]
        void OnChannelReceivedData(
            [In, MarshalAs(UnmanagedType.BStr)] string channelName,
            [In, MarshalAs(UnmanagedType.BStr)] string data);

        [DispId(8)]
        void OnRequestGoFullScreen();

        [DispId(9)]
        void OnRequestLeaveFullScreen();

        [DispId(10)]
        void OnFatalError([In] int errorCode);

        [DispId(11)]
        void OnWarning([In] int warningCode);

        [DispId(12)]
        void OnRemoteDesktopSizeChange([In] int width, [In] int height);

        [DispId(13)]
        void OnIdleTimeoutNotification();

        [DispId(14)]
        void OnRequestContainerMinimize();

        [DispId(15)]
        void OnConfirmClose([Out] out bool allowClose);

        [DispId(16)]
        void OnReceivedTSPublicKey(
            [In, MarshalAs(UnmanagedType.BStr)] string publicKey,
            [Out] out bool continueLogon);

        [DispId(17)]
        void OnAutoReconnecting(
            [In] int disconnectReason,
            [In] int attemptCount,
            [Out] out AutoReconnectContinueState continueStatus);

        [DispId(18)]
        void OnAuthenticationWarningDisplayed();

        [DispId(19)]
        void OnAuthenticationWarningDismissed();

        [DispId(20)]
        void OnRemoteProgramResult(
            [In, MarshalAs(UnmanagedType.BStr)] string remoteProgram,
            [In] RemoteProgramResult error,
            [In] bool isExecutable);

        [DispId(21)]
        void OnRemoteProgramDisplayed(
            [In] bool displayed,
            [In] uint displayInformation);

        [DispId(29)]
        void OnRemoteWindowDisplayed(
            [In] bool displayed,
            [In] ref RemotableHandle windowHandle,
            [In] RemoteWindowDisplayedAttribute windowAttribute);

        [DispId(22)]
        void OnLogonError([In] int errorCode);

        [DispId(23)]
        void OnFocusReleased([In] int direction);

        [DispId(24)]
        void OnUserNameAcquired(
            [In, MarshalAs(UnmanagedType.BStr)] string userName);

        [DispId(26)]
        void OnMouseInputModeChanged([In] bool isRelativeMouseMode);

        [DispId(28)]
        void OnServiceMessageReceived(
            [In, MarshalAs(UnmanagedType.BStr)] string serviceMessage);

        [DispId(30)]
        void OnConnectionBarPullDown();

        [DispId(32)]
        void OnNetworkStatusChanged(
            [In] uint qualityLevel,
            [In] int bandwidth,
            [In] int roundTripTime);

        [DispId(35)]
        void OnDevicesButtonPressed();

        [DispId(33)]
        void OnAutoReconnected();

        [DispId(34)]
        void OnAutoReconnecting2(
            [In] int disconnectReason,
            [In] bool networkAvailable,
            [In] int attemptCount,
            [In] int maxAttemptCount);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RdpEventSink(RdpActiveXHost owner) : IMsTscAxEvents
    {
        public void OnConnecting()
        {
        }

        public void OnConnected()
        {
        }

        public void OnLoginComplete()
        {
            owner.OnLoginComplete();
        }

        public void OnDisconnected(int disconnectReason)
        {
            owner.OnDisconnected(disconnectReason);
        }

        public void OnEnterFullScreenMode()
        {
        }

        public void OnLeaveFullScreenMode()
        {
        }

        public void OnChannelReceivedData(string channelName, string data)
        {
        }

        public void OnRequestGoFullScreen()
        {
        }

        public void OnRequestLeaveFullScreen()
        {
        }

        public void OnFatalError(int errorCode)
        {
            owner.OnFatalError(errorCode);
        }

        public void OnWarning(int warningCode)
        {
        }

        public void OnRemoteDesktopSizeChange(int width, int height)
        {
        }

        public void OnIdleTimeoutNotification()
        {
        }

        public void OnRequestContainerMinimize()
        {
        }

        public void OnConfirmClose(out bool allowClose)
        {
            allowClose = true;
        }

        public void OnReceivedTSPublicKey(
            string publicKey,
            out bool continueLogon)
        {
            continueLogon = true;
        }

        public void OnAutoReconnecting(
            int disconnectReason,
            int attemptCount,
            out AutoReconnectContinueState continueStatus)
        {
            continueStatus = AutoReconnectContinueState.Automatic;
        }

        public void OnAuthenticationWarningDisplayed()
        {
        }

        public void OnAuthenticationWarningDismissed()
        {
        }

        public void OnRemoteProgramResult(
            string remoteProgram,
            RemoteProgramResult error,
            bool isExecutable)
        {
        }

        public void OnRemoteProgramDisplayed(
            bool displayed,
            uint displayInformation)
        {
        }

        public void OnRemoteWindowDisplayed(
            bool displayed,
            ref RemotableHandle windowHandle,
            RemoteWindowDisplayedAttribute windowAttribute)
        {
        }

        public void OnLogonError(int errorCode)
        {
            owner.OnLogonError(errorCode);
        }

        public void OnFocusReleased(int direction)
        {
        }

        public void OnUserNameAcquired(string userName)
        {
        }

        public void OnMouseInputModeChanged(bool isRelativeMouseMode)
        {
        }

        public void OnServiceMessageReceived(string serviceMessage)
        {
        }

        public void OnConnectionBarPullDown()
        {
        }

        public void OnNetworkStatusChanged(
            uint qualityLevel,
            int bandwidth,
            int roundTripTime)
        {
        }

        public void OnDevicesButtonPressed()
        {
        }

        public void OnAutoReconnected()
        {
            owner.OnLoginComplete();
        }

        public void OnAutoReconnecting2(
            int disconnectReason,
            bool networkAvailable,
            int attemptCount,
            int maxAttemptCount)
        {
        }
    }

    private enum AutoReconnectContinueState
    {
        Automatic,
        Stop,
        Manual
    }

    private enum RemoteProgramResult
    {
        Ok,
        Locked,
        ProtocolError,
        NotInWhitelist,
        NetworkPathDenied,
        FileNotFound,
        Failure,
        HookNotLoaded
    }

    private enum RemoteWindowDisplayedAttribute
    {
        None,
        WindowDisplayed,
        ShellIconDisplayed
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RemotableHandle
    {
        internal int Context;
        internal RemotableHandleUnion Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RemotableHandleUnion
    {
        [FieldOffset(0)]
        internal int InProcessHandle;

        [FieldOffset(0)]
        internal int RemoteHandle;
    }

    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    [ComImport]
    [Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpClientNonScriptable
    {
        void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortablePassword();

        void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortableSalt();

        void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinaryPassword();

        void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinarySalt();

        void ResetPassword();

        void NotifyRedirectDeviceChange(nuint wParam, nint lParam);

        void SendKeys(
            int numKeys,
            [In] ref short keyUpStates,
            [In] ref int keyData);
    }

    private readonly record struct RemoteKey(int ScanCode, bool IsExtended);

    private readonly record struct KeyStroke(RemoteKey Key, bool IsKeyUp);
}
