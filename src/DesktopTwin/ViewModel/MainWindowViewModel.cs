using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopTwin.Models;
using DesktopTwin.Services;
using DesktopTwin.View;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DesktopTwin.ViewModel;

/// <summary>
/// 主窗口 ViewModel：把 <see cref="ChildSessionService"/> 的状态映射为界面可绑定的属性与命令。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private const double DefaultWindowWidth = 1280d;
    private const double SmallWindowWidth = 500d;

    private readonly ChildSessionService _childSessionService;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _notificationTimer;
    private readonly UuViewSwitchService _uuViewSwitchService;
    private GlobalHotkeyService? _globalHotkey;
    private Window? _hotkeyWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _startRequested;
    private bool _uuSwitchInProgress;

    [ObservableProperty]
    private Brush _connectionStatusBrush = Brushes.Red;

    [ObservableProperty]
    private string _connectionStatusToolTip = "桌面分身未启动";

    [ObservableProperty]
    private string _connectionStatusTitle = "桌面分身尚未启动";

    [ObservableProperty]
    private string _connectionStatusDescription =
        "点击“启动并连接”，程序会创建一个独立的 Windows 桌面，并把画面嵌到这个窗口里。";

    [ObservableProperty]
    private string _rdpStatusText = "RDP 未连接";

    [ObservableProperty]
    private string _childSessionStatusText = "分身会话未创建";

    [ObservableProperty]
    private bool _isRdpConnected;

    [ObservableProperty]
    private bool _isConnectionPromptVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionInteractionEnabled))]
    private bool _isSessionClosing;

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isAdaptive = true;

    [ObservableProperty]
    private bool _isOneToOne;

    [ObservableProperty]
    private bool _keepAspectRatio = true;

    [ObservableProperty]
    private WindowPosition? _normalWindowPosition;

    [ObservableProperty]
    private WindowPosition? _smallWindowPosition;

    [ObservableProperty]
    private int _smallWindowResizeRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowSizeModeMenuHeader))]
    [NotifyPropertyChangedFor(nameof(WindowSizeModeMenuToolTip))]
    [NotifyPropertyChangedFor(nameof(WindowResizeTargetWidth))]
    private bool _isSmallWindowMode;

    [ObservableProperty]
    private bool _sendSystemShortcutsToRemote = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuHeader))]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuToolTip))]
    private bool _isWatchModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioButtonToolTip))]
    private bool _isAudioMuted;

    [ObservableProperty]
    private string _targetProgramText = "未选择";

    [ObservableProperty]
    private bool _autoLaunchOnConnect = true;

    [ObservableProperty]
    private bool _runAsAdministrator;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = string.Empty;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSendSystemShortcutsToRemoteCommand))]
    private bool _isSystemShortcutsReconnectPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleAudioMutedCommand))]
    private bool _isAudioReconnectPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UuViewSwitchMenuHeader))]
    private bool _uuViewSwitchEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoLoginMenuHeader))]
    private bool _autoLoginEnabled;

    [ObservableProperty]
    private bool _hasAutoLoginPassword;

    /// <summary>
    /// 创建 ViewModel 并订阅服务事件。
    /// </summary>
    public MainWindowViewModel(ChildSessionService childSessionService, SettingsService settings)
    {
        _childSessionService = childSessionService;
        _settings = settings;

        _isTopmost = childSessionService.TopmostEnabled;
        _isAdaptive = childSessionService.SmartSizingEnabled;
        _isOneToOne = !_isAdaptive;
        _keepAspectRatio = childSessionService.KeepAspectRatio;
        _normalWindowPosition = childSessionService.NormalWindowPosition;
        _smallWindowPosition = childSessionService.SmallWindowPosition;
        _sendSystemShortcutsToRemote = childSessionService.SendSystemShortcutsToRemote;
        _isAudioMuted = childSessionService.AudioMuted;
        _isWatchModeEnabled = childSessionService.WatchModeEnabled;
        _autoLaunchOnConnect = settings.Current.Launch.AutoLaunchOnConnect;
        _runAsAdministrator = settings.Current.Launch.RunAsAdministrator;
        _targetProgramText = BuildTargetProgramText(settings.Current.Launch.TargetPath);
        _isSmallWindowMode = settings.Current.Window.SmallWindowMode;

        _uuViewSwitchService = new UuViewSwitchService(childSessionService);
        _uuViewSwitchEnabled = settings.Current.Automation.UuViewSwitchEnabled;
        _autoLoginEnabled = settings.Current.Automation.AutoLoginEnabled;
        _hasAutoLoginPassword = childSessionService.AutoLogin?.HasStoredPassword == true;

        _notificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _notificationTimer.Tick += OnNotificationTimerTick;

        _childSessionService.StateChanged += OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed += OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted += OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted += OnAudioReconnectCompleted;
        _childSessionService.AutoLaunchSkipped += OnAutoLaunchSkipped;
        UpdateConnectionStatus();
    }

    /// <summary>
    /// 置顶按钮提示文本。
    /// </summary>
    public string TopmostButtonToolTip => IsTopmost ? "取消置顶" : "置顶";

    /// <summary>
    /// 音频按钮提示文本。
    /// </summary>
    public string AudioButtonToolTip => IsAudioMuted
        ? "开启分身声音，不影响主桌面的其他程序；切换时会自动重新连接 RDP"
        : "关闭分身声音，不影响主桌面的其他程序；切换时会自动重新连接 RDP";

    /// <summary>
    /// 小窗模式菜单文本。
    /// </summary>
    public string WindowSizeModeMenuHeader => IsSmallWindowMode ? "还原窗口" : "小窗模式";

    /// <summary>
    /// 小窗模式菜单提示。
    /// </summary>
    public string WindowSizeModeMenuToolTip => IsSmallWindowMode
        ? "将窗口还原至默认大小"
        : "把窗口缩到 750 宽，并按 16:9 同步缩小高度";

    /// <summary>
    /// 观看模式菜单文本。
    /// </summary>
    public string WatchModeMenuHeader => IsWatchModeEnabled ? "控制模式" : "观看模式";

    /// <summary>
    /// 观看模式菜单提示。
    /// </summary>
    public string WatchModeMenuToolTip => IsWatchModeEnabled
        ? "退出观看模式"
        : "分身画面仅可观看，本机键鼠不会生效";

    /// <summary>
    /// 尺寸切换目标宽度。
    /// </summary>
    public double WindowResizeTargetWidth => IsSmallWindowMode ? SmallWindowWidth : DefaultWindowWidth;

    /// <summary>
    /// 当前是否已连接。
    /// </summary>
    public bool IsSessionInteractionEnabled => !IsSessionClosing;

    /// <summary>
    /// 当前分辨率是否为 1920 × 1080。
    /// </summary>
    public bool IsDefaultResolutionSelected =>
        _settings.Current.Session.DesktopWidth == 1920
        && _settings.Current.Session.DesktopHeight == 1080;

    /// <summary>
    /// 当前分辨率是否为 2560 × 1440。
    /// </summary>
    public bool IsQuadHdResolutionSelected =>
        _settings.Current.Session.DesktopWidth == 2560
        && _settings.Current.Session.DesktopHeight == 1440;

    /// <summary>
    /// 当前分辨率是否为 1600 × 900。
    /// </summary>
    public bool IsHdPlusResolutionSelected =>
        _settings.Current.Session.DesktopWidth == 1600
        && _settings.Current.Session.DesktopHeight == 900;

    /// <summary>
    /// 当前分辨率是否为 1280 × 720。
    /// </summary>
    public bool IsHdResolutionSelected =>
        _settings.Current.Session.DesktopWidth == 1280
        && _settings.Current.Session.DesktopHeight == 720;

    /// <summary>
    /// 当前分辨率不在预设列表里，也就是用户自定义的尺寸。
    /// </summary>
    public bool IsCustomResolutionSelected =>
        !IsDefaultResolutionSelected
        && !IsQuadHdResolutionSelected
        && !IsHdPlusResolutionSelected
        && !IsHdResolutionSelected;

    /// <summary>
    /// 当前分辨率的显示文本，形如 <c>1920 × 1080</c>。
    /// </summary>
    public string CurrentResolutionText =>
        $"{_settings.Current.Session.DesktopWidth} × {_settings.Current.Session.DesktopHeight}";

    /// <summary>
    /// 断开 RDP 并注销分身会话，用于关闭窗口前的确认流程。
    /// </summary>
    public async Task LogoffAndHideAsync()
    {
        if (IsSessionClosing)
        {
            return;
        }

        IsSessionClosing = true;
        UpdateConnectionStatus();

        try
        {
            // 先让「正在关闭」状态完成渲染，再执行可能耗时的注销操作。
            await Dispatcher.Yield(DispatcherPriority.Render);
            await _childSessionService.LogoffAndHideAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsSessionClosing = false;
            UpdateConnectionStatus();
        }
    }

    partial void OnNormalWindowPositionChanged(WindowPosition? value)
    {
        _childSessionService.NormalWindowPosition = value;
    }

    partial void OnSmallWindowPositionChanged(WindowPosition? value)
    {
        _childSessionService.SmallWindowPosition = value;
    }

    partial void OnIsTopmostChanged(bool value)
    {
        OnPropertyChanged(nameof(TopmostButtonToolTip));
    }

    partial void OnIsSmallWindowModeChanged(bool value)
    {
        _settings.Current.Window.SmallWindowMode = value;
        _settings.SaveSoon();
    }

    partial void OnAutoLaunchOnConnectChanged(bool value)
    {
        _settings.Current.Launch.AutoLaunchOnConnect = value;
        _settings.SaveSoon();
    }

    partial void OnRunAsAdministratorChanged(bool value)
    {
        _settings.Current.Launch.RunAsAdministrator = value;
        _settings.SaveSoon();
    }

    /// <summary>
    /// 启动桌面分身。启动前依次检查环境阻塞项与风险项，用户选择不继续则中止。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!await EnsureSystemChangeDisclosedAsync())
        {
            return;
        }

        if (_childSessionService.ConnectedState != 1)
        {
            _childSessionService.RefreshEnvironmentCheck();

            // 阻塞项（例如缺少 RDP 主机）直接拒绝，避免用户白等 60 秒超时。
            if (_childSessionService.HasBlockingEnvironmentIssue)
            {
                await Dialogs.ErrorAsync(
                    _childSessionService.EnvironmentIssueSummary,
                    "无法启动桌面分身");
                return;
            }

            if (_childSessionService.HasEnvironmentIssue)
            {
                // 风险项只是提示，不应该挡住本来可以连上的用户。
                var confirmed = await Dialogs.ConfirmAsync(
                    _childSessionService.EnvironmentIssueSummary
                    + "\n\n检测到可能影响桌面分身的环境设置。是否仍要继续？",
                    "桌面分身环境检查");
                if (!confirmed)
                {
                    return;
                }
            }
        }

        _startRequested = true;
        IsConnectionPromptVisible = false;

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _childSessionService.StartAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _startRequested = false;
            UpdateConnectionStatus();
        }
    }

    /// <summary>
    /// 首次启动前，把「会修改系统级设置」这件事明确告诉用户。
    ///
    /// 这一步不能省：`WTSEnableChildSessions(true)` 是持久设置，重启后依然生效，
    /// 用户有权在第一次动手之前就知道，而不是事后从 README 里读到。
    /// </summary>
    /// <returns>用户是否同意继续。</returns>
    private async Task<bool> EnsureSystemChangeDisclosedAsync()
    {
        if (_settings.Current.ChildSessionNoticeAccepted)
        {
            return true;
        }

        var accepted = await Dialogs.ConfirmAsync(
            "启动桌面分身之前，请先了解它会做什么：\n\n"
            + "1. 本程序以管理员权限运行，并会打开 Windows 的「子会话（Child Sessions）」功能。\n"
            + "   这是一个系统级设置，关闭程序后仍然保留，重启电脑也不会自动恢复。\n"
            + "2. 你可以在「控制中心 → 诊断 → 关闭 Child Sessions（恢复系统原状）」中随时还原。\n"
            + "3. 桌面分身会创建一个与当前账户相同的第二个桌面。它不是沙盒：\n"
            + "   分身里能访问的文件和权限，与主桌面完全一样。\n\n"
            + "是否继续？",
            "首次使用说明");
        if (!accepted)
        {
            return false;
        }

        _settings.Current.ChildSessionNoticeAccepted = true;
        _settings.SaveSoon();
        return true;
    }

    [RelayCommand]
    private void Hide()
    {
        Execute(_childSessionService.HideWindow);
    }

    [RelayCommand]
    private void SwitchWindow()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    /// <summary>
    /// 选择要在分身中启动的程序。选择后立即保存，下次连接时自动启动。
    /// </summary>
    [RelayCommand]
    private void SelectTargetProgram()
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "选择要在桌面分身中启动的程序",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog() != true)
        {
            return;
        }

        var path = Path.GetFullPath(fileDialog.FileName);
        var launch = _settings.Current.Launch;
        launch.TargetPath = path;

        // 最近使用列表去重后置顶，最多保留 8 条。
        launch.RecentTargets.RemoveAll(item =>
            string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        launch.RecentTargets.Insert(0, path);
        if (launch.RecentTargets.Count > 8)
        {
            launch.RecentTargets.RemoveRange(8, launch.RecentTargets.Count - 8);
        }

        _settings.SaveSoon();
        TargetProgramText = BuildTargetProgramText(path);
        ShowNotification(
            "已选择目标程序",
            $"{Path.GetFileName(path)} 将在下次连接成功后自动启动。",
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// 在分身中立即启动当前目标程序。
    /// </summary>
    [RelayCommand]
    private async Task LaunchTargetAsync()
    {
        var launch = _settings.Current.Launch;
        if (launch.RunAsAdministrator && !string.IsNullOrWhiteSpace(launch.TargetPath))
        {
            // 提权启动等于在本机管理员上下文里执行这个文件，必须让用户看清目标再确认一次。
            var confirmed = await Dialogs.ConfirmAsync(
                $"即将以管理员权限在桌面分身中启动：\n\n{launch.TargetPath}"
                + (string.IsNullOrWhiteSpace(launch.TargetArguments)
                    ? string.Empty
                    : $"\n参数：{launch.TargetArguments}")
                + "\n\n请确认这是你要运行的程序。",
                "确认以管理员权限启动");
            if (!confirmed)
            {
                return;
            }
        }

        await ExecuteAsync(_childSessionService.LaunchTargetAsync);
    }

    /// <summary>
    /// 切换「连接成功后自动启动目标程序」。
    /// </summary>
    [RelayCommand]
    private void ToggleAutoLaunch()
    {
        AutoLaunchOnConnect = !AutoLaunchOnConnect;
    }

    /// <summary>
    /// 切换「以管理员权限启动目标程序」。
    /// </summary>
    [RelayCommand]
    private void ToggleRunAsAdministrator()
    {
        RunAsAdministrator = !RunAsAdministrator;
    }

    /// <summary>
    /// UU 视角切换菜单文本。
    /// </summary>
    public string UuViewSwitchMenuHeader => UuViewSwitchEnabled ? "UU 视角切换（已启用）" : "UU 视角切换";

    /// <summary>
    /// 自动登录菜单文本。
    /// </summary>
    public string AutoLoginMenuHeader => AutoLoginEnabled ? "自动登录分身（已启用）" : "自动登录分身";

    /// <summary>
    /// 当前 UU 切换热键的可读文本。
    /// </summary>
    public string UuHotkeyText =>
        GlobalHotkeyService.Format(_settings.Current.Automation.UuViewSwitchHotkey);

    /// <summary>
    /// 把窗口交给 ViewModel，用于注册全局热键。窗口创建后调用一次。
    /// </summary>
    /// <param name="window">主窗口。</param>
    internal void AttachWindow(Window window)
    {
        _hotkeyWindow = window;
        ApplyUuHotkey(notifySuccess: false);
    }

    /// <summary>
    /// 窗口关闭时释放热键与后台服务。
    /// </summary>
    internal void Detach()
    {
        _globalHotkey?.Dispose();
        _globalHotkey = null;
        _uuViewSwitchService.Dispose();
    }

    /// <summary>
    /// 立即执行一次视角切换。菜单项和热键走同一条路径。
    /// </summary>
    [RelayCommand]
    private async Task TriggerUuViewSwitchAsync()
    {
        if (_uuSwitchInProgress)
        {
            return;
        }

        _uuSwitchInProgress = true;
        try
        {
            ShowNotification(
                "正在切换视角",
                "正在调整会话落点，请稍候…",
                InfoBarSeverity.Informational,
                TimeSpan.FromSeconds(3));

            var result = await _uuViewSwitchService.ToggleAsync(CancellationToken.None);
            ShowNotification(
                result.Success ? "视角切换完成" : "视角切换失败",
                result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            _uuSwitchInProgress = false;
        }
    }

    /// <summary>
    /// 启用 / 关闭 UU 视角切换热键。
    /// </summary>
    [RelayCommand]
    private void ToggleUuViewSwitch()
    {
        UuViewSwitchEnabled = !UuViewSwitchEnabled;
        _settings.Current.Automation.UuViewSwitchEnabled = UuViewSwitchEnabled;
        _settings.SaveSoon();

        if (UuViewSwitchEnabled)
        {
            ApplyUuHotkey(notifySuccess: true);
            return;
        }

        _globalHotkey?.Unregister();
        ShowNotification(
            "已关闭 UU 视角切换",
            "热键已注销，不会影响其他程序。",
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 录制新的切换热键。
    /// </summary>
    [RelayCommand]
    private async Task ConfigureUuHotkeyAsync()
    {
        var current = _settings.Current.Automation.UuViewSwitchHotkey;
        if (await HotkeyDialog.ShowAsync(current) is not { } combination)
        {
            return;
        }

        _settings.Current.Automation.UuViewSwitchHotkey = combination;
        _settings.SaveSoon();
        OnPropertyChanged(nameof(UuHotkeyText));

        // 录了热键就是要用它：顺手把开关打开，省得用户再找一次菜单。
        UuViewSwitchEnabled = true;
        _settings.Current.Automation.UuViewSwitchEnabled = true;
        _settings.SaveSoon();
        ApplyUuHotkey(notifySuccess: true);
    }

    /// <summary>
    /// 全屏时提示「分身分辨率与屏幕分辨率不一致」。
    /// </summary>
    /// <param name="screenWidth">屏幕物理宽度。</param>
    /// <param name="screenHeight">屏幕物理高度。</param>
    internal void NotifyFullscreenResolution(int screenWidth, int screenHeight)
    {
        var session = _settings.Current.Session;
        if (session.DesktopWidth == screenWidth && session.DesktopHeight == screenHeight)
        {
            return;
        }

        ShowNotification(
            "分辨率与屏幕不一致",
            $"桌面分身当前是 {session.DesktopWidth} × {session.DesktopHeight}，"
            + $"屏幕是 {screenWidth} × {screenHeight}。全屏下画面会被拉伸，内容可能模糊；"
            + $"建议在「控制中心 → 桌面分辨率」里改为 {screenWidth} × {screenHeight}。",
            InfoBarSeverity.Warning,
            TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// 打开诊断与修复面板。已经打开时把它叫到前面并刷新一次。
    /// </summary>
    [RelayCommand]
    private void OpenDiagnostics()
    {
        if (_diagnosticsWindow is { IsLoaded: true } existing)
        {
            existing.RefreshNow();
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new DiagnosticsWindow();
        window.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow = window;
        Dialogs.Show(window);
    }

    /// <summary>
    /// 打开「自动登录方式」对话框：让用户在本程序自动登录与 Windows 系统自动登录之间自己选。
    /// </summary>
    [RelayCommand]
    private async Task ShowAutoLoginOptionsAsync()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            ShowNotification("自动登录不可用", "当前运行环境没有初始化自动登录服务。", InfoBarSeverity.Warning);
            return;
        }

        var systemEnabled = autoLogin.IsSystemAutoLoginEnabled(out var systemUser);
        var choice = await AutoLoginDialog.ShowAsync(
            AutoLoginEnabled,
            HasAutoLoginPassword,
            autoLogin.UserName,
            systemEnabled,
            systemUser);

        switch (choice)
        {
            case AutoLoginDialog.Choice.EnableAppAutoLogin:
                if (!HasAutoLoginPassword && !await TrySaveAndVerifyPasswordAsync())
                {
                    return;
                }

                EnableAppAutoLogin();
                break;

            case AutoLoginDialog.Choice.OpenSystemAutoLogin:
                OpenSystemAutoLoginSettings();
                break;
        }
    }

    /// <summary>
    /// 直接打开 Windows 的 netplwiz，配置系统级自动登录。
    /// </summary>
    [RelayCommand]
    private void OpenSystemAutoLoginSettings()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            return;
        }

        if (!autoLogin.TryOpenSystemAutoLoginSettings(out var error))
        {
            ShowNotification("打开失败", error, InfoBarSeverity.Warning, TimeSpan.FromSeconds(7));
            return;
        }

        ShowNotification(
            "已打开系统设置",
            "取消勾选「要使用本计算机，用户必须输入用户名和密码」，点「应用」后输入账户密码（不是 PIN），重启电脑生效。",
            InfoBarSeverity.Informational,
            TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// 启用本程序自带的自动登录（调用方负责先确保密码已保存）。
    /// </summary>
    private void EnableAppAutoLogin()
    {
        var autoLogin = _childSessionService.AutoLogin;
        AutoLoginEnabled = true;
        _settings.Current.Automation.AutoLoginEnabled = true;
        _settings.SaveSoon();
        ShowNotification(
            "已启用自动登录",
            $"将使用账户「{autoLogin?.UserName}」的登录密码。若凭据框仍然出现，程序会尝试自动填写，最多两次。",
            InfoBarSeverity.Success,
            TimeSpan.FromSeconds(7));
    }

    /// <summary>
    /// 启用 / 关闭分身自动登录。
    /// </summary>
    [RelayCommand]
    private async Task ToggleAutoLoginAsync()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            ShowNotification("自动登录不可用", "当前运行环境没有初始化自动登录服务。", InfoBarSeverity.Warning);
            return;
        }

        if (AutoLoginEnabled)
        {
            AutoLoginEnabled = false;
            _settings.Current.Automation.AutoLoginEnabled = false;
            _settings.SaveSoon();
            ShowNotification(
                "已关闭自动登录",
                "重连分身时会重新要求手动输入密码。",
                InfoBarSeverity.Informational);
            return;
        }

        // 没有密码就先要一个：只开开关不存密码，等于开了个没用的开关。
        if (!autoLogin.HasStoredPassword && !await TrySaveAndVerifyPasswordAsync())
        {
            return;
        }

        EnableAppAutoLogin();
    }

    /// <summary>
    /// 设置（或更新）自动登录密码，并立即校验一次。
    /// </summary>
    [RelayCommand]
    private async Task SetAutoLoginPasswordAsync()
    {
        _ = await TrySaveAndVerifyPasswordAsync();
    }

    /// <summary>
    /// 清除已保存的自动登录密码，并关闭自动登录。
    /// </summary>
    [RelayCommand]
    private void ClearAutoLoginPassword()
    {
        _childSessionService.AutoLogin?.ClearPassword();
        HasAutoLoginPassword = false;
        AutoLoginEnabled = false;
        _settings.Current.Automation.AutoLoginEnabled = false;
        _settings.SaveSoon();
        ShowNotification(
            "已清除密码",
            "自动登录已关闭，重连分身时需要手动输入密码。",
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 弹出密码框、加密保存，并立即验证一次。
    ///
    /// 验证方式：用 <c>CredUIPromptForWindowsCredentials</c> 弹一个**独立的**
    /// 「Windows 安全中心」凭据框（不碰分身会话），自动填充后校验提交上来的那组凭据。
    /// 这样既验证了密码本身，也验证了自动填充这条链路——而且不会打断正在用的分身。
    /// </summary>
    /// <returns>验证通过返回 true。</returns>
    private async Task<bool> TrySaveAndVerifyPasswordAsync()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            return false;
        }

        if (await PasswordDialog.ShowAsync(autoLogin.UserName) is not { } password)
        {
            return false;
        }

        if (!autoLogin.SavePassword(password))
        {
            ShowNotification(
                "保存失败",
                "无法加密保存密码，请查看日志。自动登录不会启用。",
                InfoBarSeverity.Error);
            return false;
        }

        HasAutoLoginPassword = true;

        ShowNotification(
            "正在验证",
            "即将弹出 Windows 凭据框，程序会自动填入刚保存的密码并提交。",
            InfoBarSeverity.Informational,
            TimeSpan.FromSeconds(4));

        // 强制开启监听：此刻自动登录开关可能还没打开，但同样需要自动填充。
        autoLogin.StartWatching(force: true);
        CredentialPromptService.PromptResult prompt;
        try
        {
            prompt = await CredentialPromptService.PromptAsync(
                GetOwnerWindowHandle(),
                TimeSpan.FromSeconds(90),
                CancellationToken.None);
        }
        finally
        {
            autoLogin.StopWatching();
        }

        if (prompt.Canceled)
        {
            ShowNotification(
                "已取消验证",
                "密码已保存，但没有验证。可以稍后再点一次「设置登录密码…」。",
                InfoBarSeverity.Informational,
                TimeSpan.FromSeconds(6));
            return false;
        }

        if (!prompt.Success)
        {
            ShowNotification("验证未完成", prompt.Message, InfoBarSeverity.Warning, TimeSpan.FromSeconds(10));
            return false;
        }

        var check = autoLogin.VerifyPassword(
            prompt.UserName,
            prompt.Domain,
            prompt.Password,
            out var errorCode);

        if (check == AutoLoginService.PasswordCheckResult.Valid)
        {
            ShowNotification(
                "验证通过",
                "凭据有效，自动填充也已生效。下次重连分身时会直接使用这组密码。",
                InfoBarSeverity.Success,
                TimeSpan.FromSeconds(7));
            return true;
        }

        ShowNotification(
            "密码校验未通过",
            BuildVerifyFailureMessage(check, errorCode, autoLogin),
            InfoBarSeverity.Warning,
            TimeSpan.FromSeconds(14));
        return false;
    }

    /// <summary>
    /// 取主窗口句柄，用作凭据框的父窗口。窗口尚未创建时返回 0。
    /// </summary>
    private IntPtr GetOwnerWindowHandle()
    {
        var window = _childSessionService.Window;
        if (window is null)
        {
            return IntPtr.Zero;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return handle;
    }

    /// <summary>
    /// 把校验失败翻译成可执行的说明。
    ///
    /// 特别注意 1326 不能直接断言「密码错了」：微软账户的本机凭据缓存过期时，
    /// 即使密码是新的也会返回同一个错误，两者必须一起提示。
    /// </summary>
    private static string BuildVerifyFailureMessage(
        AutoLoginService.PasswordCheckResult result,
        int errorCode,
        AutoLoginService autoLogin)
    {
        return result switch
        {
            AutoLoginService.PasswordCheckResult.AccountDisabled =>
                "该 Windows 账户已被禁用，无法用于登录。请先启用账户。",
            AutoLoginService.PasswordCheckResult.PasswordExpired =>
                "账户密码已过期，请先修改密码再重新设置。",
            AutoLoginService.PasswordCheckResult.AccountRestricted =>
                "账户受限：可能是空密码、登录时段或工作站限制。请给账户设置密码后再试。",
            AutoLoginService.PasswordCheckResult.InvalidPassword =>
                $"用户名或密码错误（错误 {errorCode}）。如果密码确实是对的，"
                + "可能是本机缓存的微软账户凭据已过期，用下面这条命令刷新一次再重试："
                + Environment.NewLine + Environment.NewLine
                + autoLogin.BuildPasswordSyncCommand()
                + Environment.NewLine + Environment.NewLine
                + "（执行后输入一次账户密码，命令行不会有任何提示，属正常现象）",
            _ =>
                $"无法校验这组凭据（错误 {errorCode}）。请确认账户类型后重试。"
        };
    }

    /// <summary>
    /// 注册或注销 UU 切换热键。
    /// </summary>
    /// <param name="notifySuccess">注册成功时是否提示用户。</param>
    private void ApplyUuHotkey(bool notifySuccess)
    {
        if (_hotkeyWindow is null)
        {
            return;
        }

        _globalHotkey ??= new GlobalHotkeyService();
        _globalHotkey.Pressed -= OnUuHotkeyPressed;
        _globalHotkey.Pressed += OnUuHotkeyPressed;
        _globalHotkey.Unregister();

        if (!UuViewSwitchEnabled)
        {
            return;
        }

        var combination = _settings.Current.Automation.UuViewSwitchHotkey;
        if (_globalHotkey.TryRegister(_hotkeyWindow, combination, out var error))
        {
            if (notifySuccess)
            {
                ShowNotification(
                    "UU 视角切换已启用",
                    $"按 {GlobalHotkeyService.Format(combination)} 可在「整台电脑」与「桌面分身」之间切换。",
                    InfoBarSeverity.Success);
            }

            return;
        }

        // 注册失败就把开关退回去，避免界面显示「已启用」但按键没反应。
        UuViewSwitchEnabled = false;
        _settings.Current.Automation.UuViewSwitchEnabled = false;
        _settings.SaveSoon();
        ShowNotification("快捷键注册失败", error, InfoBarSeverity.Warning, TimeSpan.FromSeconds(7));
    }

    private void OnUuHotkeyPressed(object? sender, EventArgs e)
    {
        _ = TriggerUuViewSwitchAsync();
    }


    /// <summary>
    /// 切换分辨率。参数形如 <c>1920x1080</c>。
    /// </summary>
    [RelayCommand]
    private void SelectResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return;
        }

        var parts = resolution.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return;
        }

        if (!Execute(() => _childSessionService.SetDesktopSize(width, height)))
        {
            return;
        }

        NotifyResolutionChanged();
    }

    /// <summary>
    /// 打开对话框输入自定义分辨率。
    /// </summary>
    [RelayCommand]
    private async Task CustomResolutionAsync()
    {
        var session = _settings.Current.Session;
        if (await ResolutionDialog.ShowAsync(session.DesktopWidth, session.DesktopHeight) is not { } size)
        {
            return;
        }

        if (!Execute(() => _childSessionService.SetDesktopSize(size.Width, size.Height)))
        {
            return;
        }

        NotifyResolutionChanged();
    }

    private void NotifyResolutionChanged()
    {
        OnPropertyChanged(nameof(IsDefaultResolutionSelected));
        OnPropertyChanged(nameof(IsQuadHdResolutionSelected));
        OnPropertyChanged(nameof(IsHdPlusResolutionSelected));
        OnPropertyChanged(nameof(IsHdResolutionSelected));
        OnPropertyChanged(nameof(IsCustomResolutionSelected));
        OnPropertyChanged(nameof(CurrentResolutionText));
    }

    [RelayCommand]
    private void UseAdaptive()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(true)))
        {
            return;
        }

        IsAdaptive = true;
        IsOneToOne = false;
    }

    [RelayCommand]
    private void UseOneToOne()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(false)))
        {
            return;
        }

        IsAdaptive = false;
        IsOneToOne = true;
    }

    [RelayCommand]
    private void ToggleSmallWindowMode()
    {
        IsSmallWindowMode = !IsSmallWindowMode;
        SmallWindowResizeRequest++;
    }

    [RelayCommand]
    private void ToggleKeepAspectRatio()
    {
        if (!Execute(() => _childSessionService.SetKeepAspectRatio(!KeepAspectRatio)))
        {
            return;
        }

        KeepAspectRatio = _childSessionService.KeepAspectRatio;
    }

    [RelayCommand(CanExecute = nameof(CanToggleSendSystemShortcutsToRemote))]
    private void ToggleSendSystemShortcutsToRemote()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetSendSystemShortcutsToRemote(
                !SendSystemShortcutsToRemote);
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            IsSystemShortcutsReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "正在应用设置" : "设置已保存",
                reconnectStarted
                    ? "RDP 正在自动重新连接，连接完成后系统组合键设置生效。"
                    : "当前没有分身会话，系统组合键设置将在下次连接时生效。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsSystemShortcutsReconnectPending = false;
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            ShowNotification(
                "设置切换失败",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleSendSystemShortcutsToRemote()
    {
        return !IsSystemShortcutsReconnectPending;
    }

    [RelayCommand]
    private void ToggleWatchMode()
    {
        var enabled = !IsWatchModeEnabled;
        if (!Execute(() => _childSessionService.SetWatchModeEnabled(enabled)))
        {
            IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
            return;
        }

        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        ShowNotification(
            enabled ? "观看模式已开启" : "观看模式已关闭",
            enabled
                ? "本机键鼠不会对分身生效，但控制中心的组合键仍然生效。"
                : "本机键鼠恢复操作桌面分身。",
            enabled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(CanToggleAudioMuted))]
    private void ToggleAudioMuted()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetAudioMuted(!IsAudioMuted);
            IsAudioMuted = _childSessionService.AudioMuted;
            IsAudioReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "正在应用音频设置" : "音频设置已保存",
                reconnectStarted
                    ? "RDP 正在自动重新连接，连接完成后音频设置生效。"
                    : IsAudioMuted
                        ? "分身将在下次连接时保持静音。"
                        : "分身将在下次连接时播放声音。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsAudioReconnectPending = false;
            IsAudioMuted = _childSessionService.AudioMuted;
            ShowNotification(
                "音频设置切换失败",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleAudioMuted()
    {
        return !IsAudioReconnectPending;
    }

    [RelayCommand]
    private void ShowDesktop()
    {
        Execute(_childSessionService.ShowChildSessionDesktop);
    }

    [RelayCommand]
    private void ShowTaskView()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    [RelayCommand]
    private void ToggleTopmost()
    {
        if (!Execute(() => _childSessionService.SetTopmost(!IsTopmost)))
        {
            return;
        }

        IsTopmost = _childSessionService.TopmostEnabled;
    }

    /// <summary>
    /// 把环境快照复制到剪贴板，方便用户在反馈问题时直接粘贴。
    /// </summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var snapshot = ChildSessionEnvironmentCheck.BuildDiagnosticSnapshot();
            Clipboard.SetText(snapshot);
            ShowNotification(
                "环境信息已复制",
                "已复制到剪贴板，可以直接粘贴到 Issue 中。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>
    /// 打开日志目录。
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        Execute(() => OpenDirectory(AppLog.LogDirectory));
    }

    /// <summary>
    /// 打开设置文件所在目录。
    /// </summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        Execute(() => OpenDirectory(_settings.SettingsDirectory));
    }

    /// <summary>
    /// 用资源管理器打开目录。
    ///
    /// 这里刻意不直接 ShellExecute 目录路径：本程序以管理员权限运行，
    /// 对用户可写目录做 ShellExecute 会执行该目录下的 autorun 类处理程序。
    /// </summary>
    private static void OpenDirectory(string directory)
    {
        Directory.CreateDirectory(directory);

        // 必须给出 explorer.exe 的完整路径：UseShellExecute=false 走 CreateProcess 的搜索顺序，
        // 第一站是「本程序所在目录」。本程序以管理员权限运行，如果被解压到 Downloads 这类
        // 用户可写目录，同目录放一个 explorer.exe 就能在管理员上下文里执行。
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var startInfo = new ProcessStartInfo(explorerPath)
        {
            UseShellExecute = false
        };

        // 用 ArgumentList 而不是手工拼引号，避免目录名含引号时参数被截断。
        startInfo.ArgumentList.Add(directory);
        _ = Process.Start(startInfo);
    }

    /// <summary>
    /// 关闭 Windows 的 Child Sessions 功能，恢复系统原状。
    /// </summary>
    [RelayCommand]
    private async Task DisableChildSessionsAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            "关闭 Child Sessions 会撤销本程序对 Windows 的修改，之后本机将无法再创建第二个桌面会话，"
            + "直到再次启动桌面分身。\n\n当前分身会被注销，其中正在运行的程序会被关闭。是否继续？",
            "关闭 Child Sessions");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _childSessionService.LogoffAndHideAsync();
            _childSessionService.DisableChildSessionsFeature();
            ShowNotification(
                "已关闭 Child Sessions",
                "系统已恢复原状，下次启动桌面分身时会重新启用。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private bool Execute(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private void OnChildSessionStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            UpdateConnectionStatus();
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(UpdateConnectionStatus);
    }

    private void OnChildSessionConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                IsAudioReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "RDP 连接失败",
                    e.Message + " 已打开诊断面板，可直接查看可能的原因。",
                    InfoBarSeverity.Error,
                    TimeSpan.FromSeconds(10));

                // 连接失败时自动弹诊断面板：RDP ActiveX 只会回报「发生内部错误」，
                // 真正的原因几乎都在环境检查里，让用户自己去菜单里找太绕。
                OpenDiagnostics();
            }));
    }

    private void OnSystemShortcutsReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "设置已生效",
                    SendSystemShortcutsToRemote
                        ? "系统组合键现在会发送到桌面分身。"
                        : "系统组合键现在会在本机生效。",
                    InfoBarSeverity.Success);
            }));
    }

    private void OnAutoLaunchSkipped(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                ShowNotification(
                    "已跳过自动启动",
                    "自动启动已被跳过：目标程序要求管理员权限，或当前系统（UAC 未启用）无法真正降权。"
                    + "出于安全考虑不会自动执行，请用「控制中心 → 立即在分身中启动」手动确认一次。",
                    InfoBarSeverity.Warning,
                    TimeSpan.FromSeconds(10));
            }));
    }

    private void OnAudioReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsAudioReconnectPending = false;
                IsAudioMuted = _childSessionService.AudioMuted;
                UpdateConnectionStatus();
                ShowNotification(
                    IsAudioMuted ? "分身已静音" : "分身声音已开启",
                    IsAudioMuted
                        ? "仅分身的声音已关闭，主桌面其他程序不受影响。"
                        : "分身的声音已恢复。",
                    InfoBarSeverity.Success);
            }));
    }

    private void ShowNotification(
        string title,
        string message,
        InfoBarSeverity severity,
        TimeSpan? duration = null)
    {
        _notificationTimer.Stop();
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
        _notificationTimer.Interval = duration ?? TimeSpan.FromSeconds(5);
        _notificationTimer.Start();
    }

    private void OnNotificationTimerTick(object? sender, EventArgs e)
    {
        _notificationTimer.Stop();
        IsNotificationOpen = false;
    }

    private void UpdateConnectionStatus()
    {
        var connectedState = _childSessionService.ConnectedState;
        var childSessionId = _childSessionService.ChildSessionId;
        IsTopmost = _childSessionService.TopmostEnabled;
        IsAdaptive = _childSessionService.SmartSizingEnabled;
        IsOneToOne = !IsAdaptive;
        KeepAspectRatio = _childSessionService.KeepAspectRatio;
        SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        IsAudioMuted = _childSessionService.AudioMuted;
        IsRdpConnected = connectedState == 1;
        IsConnectionPromptVisible = IsSessionClosing || (connectedState == 0 && !_startRequested);

        if (IsSessionClosing)
        {
            ConnectionStatusBrush = Brushes.Orange;
            ConnectionStatusTitle = "正在关闭桌面分身";
            ConnectionStatusDescription = "正在断开 RDP 并注销分身会话，此过程可能需要一些时间。";
            RdpStatusText = connectedState == 0 ? "RDP 已断开" : "正在断开 RDP";
            ChildSessionStatusText = childSessionId is null
                ? "正在确认分身会话已注销"
                : $"正在注销分身会话 {childSessionId.Value}";
            ConnectionStatusToolTip = "关闭期间已暂停桌面分身操作，完成后窗口会自动隐藏。";
            return;
        }

        if (IsRdpConnected)
        {
            ConnectionStatusBrush = Brushes.LimeGreen;
            ConnectionStatusTitle = "桌面分身已连接";
            ConnectionStatusDescription = "RDP 连接正常，可以直接在这个窗口里操作分身桌面。";
        }
        else if (connectedState == 2)
        {
            ConnectionStatusBrush = childSessionId is null ? Brushes.Red : Brushes.DodgerBlue;
            ConnectionStatusTitle = "正在连接桌面分身";
            ConnectionStatusDescription = "正在创建或恢复 RDP 连接，请稍候。";
        }
        else if (childSessionId is not null)
        {
            ConnectionStatusBrush = Brushes.DodgerBlue;
            ConnectionStatusTitle = "桌面分身已启动，等待连接";
            ConnectionStatusDescription = "分身会话仍在运行，点击“启动并连接”可重新建立 RDP 连接。";
        }
        else
        {
            ConnectionStatusBrush = Brushes.Red;
            ConnectionStatusTitle = "桌面分身尚未启动";
            ConnectionStatusDescription =
                "点击“启动并连接”，程序会创建一个独立的 Windows 桌面，并把画面嵌到这个窗口里。";
        }

        RdpStatusText = connectedState switch
        {
            0 => "RDP 未连接",
            1 => "RDP 已连接",
            2 => "RDP 正在连接",
            _ => $"RDP 状态未知（{connectedState}）"
        };
        ChildSessionStatusText = childSessionId is null
            ? "分身会话未创建"
            : $"分身会话 {childSessionId.Value}";
        ConnectionStatusToolTip = _childSessionService.StatusText;
    }

    private static string BuildTargetProgramText(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "未选择" : Path.GetFileName(path);
    }

    private static void ShowError(Exception exception)
    {
        var actualException = exception.GetBaseException();
        var suggestion = actualException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
            ? "\n\n操作被系统拒绝，请确认 DesktopTwin 正在以管理员权限运行。"
            : string.Empty;

        AppLog.Error("界面操作失败。", actualException);
        Dialogs.ShowError(
            $"{actualException.Message}{suggestion}",
            "桌面分身");
    }
}
