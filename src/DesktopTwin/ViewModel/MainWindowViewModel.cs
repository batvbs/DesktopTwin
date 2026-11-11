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
using DesktopTwin.Localization;
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
    private readonly SessionAgentService _sessionAgent;
    private long _lastAgentStartTicks;
    private GlobalHotkeyService? _globalHotkey;
    private Window? _hotkeyWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _startRequested;
    private bool _uuSwitchInProgress;

    [ObservableProperty]
    private Brush _connectionStatusBrush = Brushes.Red;

    [ObservableProperty]
    private string _connectionStatusToolTip = Locale.Get("Main.Status.ToolTip");

    [ObservableProperty]
    private string _connectionStatusTitle = Locale.Get("Main.Status.NotStarted");

    [ObservableProperty]
    private string _connectionStatusDescription =
        Locale.Get("Main.Status.Description");

    [ObservableProperty]
    private string _rdpStatusText = Locale.Get("Main.Rdp.NotConnected");

    [ObservableProperty]
    private string _childSessionStatusText = Locale.Get("Main.Session.None");

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
    private string _targetProgramText = Locale.Get("Menu.TargetProgram.None");

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

        // 分身内的热键代理：Console 侧的热键在焦点进分身画面后会失效，
        // 必须由分身会话里的这个代理接住，再回头通知主端执行切换。
        _sessionAgent = new SessionAgentService(Dispatcher.CurrentDispatcher);
        _sessionAgent.SwitchRequested += OnSessionAgentSwitchRequested;

        // 切换后串流端被自动恢复成功时告诉用户一声，免得用户以为得手动重连。
        _uuViewSwitchService.StreamerRecovered += OnStreamerRecovered;

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
        _childSessionService.DesktopSizeChanged += OnDesktopSizeChanged;
        UpdateConnectionStatus();
    }

    /// <summary>
    /// 置顶按钮提示文本。
    /// </summary>
    public string TopmostButtonToolTip => IsTopmost ? Locale.Get("Main.Topmost.Off") : Locale.Get("Main.Topmost.On");

    /// <summary>
    /// 音频按钮提示文本。
    /// </summary>
    public string AudioButtonToolTip => IsAudioMuted
        ? Locale.Get("Main.Audio.Unmute")
        : Locale.Get("Main.Audio.Mute");

    /// <summary>
    /// 小窗模式菜单文本。
    /// </summary>
    public string WindowSizeModeMenuHeader => IsSmallWindowMode ? Locale.Get("Main.SmallWindow.Restore") : Locale.Get("Main.SmallWindow.Enable");

    /// <summary>
    /// 小窗模式菜单提示。
    /// </summary>
    public string WindowSizeModeMenuToolTip => IsSmallWindowMode
        ? Locale.Get("Main.SmallWindow.Restore.ToolTip")
        : Locale.Get("Main.SmallWindow.Enable.ToolTip");

    /// <summary>
    /// 观看模式菜单文本。
    /// </summary>
    public string WatchModeMenuHeader => IsWatchModeEnabled ? Locale.Get("Main.WatchMode.Control") : Locale.Get("Main.WatchMode.Watch");

    /// <summary>
    /// 观看模式菜单提示。
    /// </summary>
    public string WatchModeMenuToolTip => IsWatchModeEnabled
        ? Locale.Get("Main.WatchMode.Exit.ToolTip")
        : Locale.Get("Main.WatchMode.Enter.ToolTip");

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

        // 打开自动启动时如果已经开着「管理员权限」，要补一次明确授权
        // （用户可能是先勾了提权、再勾自动启动）。
        if (value && _settings.Current.Launch.RunAsAdministrator)
        {
            _ = EnsureElevatedAutoLaunchConsentAsync(revertAutoLaunch: true);
        }
    }

    partial void OnRunAsAdministratorChanged(bool value)
    {
        _settings.Current.Launch.RunAsAdministrator = value;
        _settings.SaveSoon();

        // 「自动启动 + 提权」= 每次连上分身都会以管理员权限跑这个程序。
        // 这是一条需要用户明确点头的路径，所以在这里要一次确认，
        // 确认结果与目标程序绑定后存下来，后面就不再拦。
        if (value && _settings.Current.Launch.AutoLaunchOnConnect)
        {
            _ = EnsureElevatedAutoLaunchConsentAsync(revertAutoLaunch: false);
        }
        else if (!value)
        {
            // 关掉提权就不需要这份授权了，顺手清掉，避免以后误用。
            _settings.Current.Launch.ElevatedAutoLaunchConsent = null;
            _settings.SaveSoon();
        }
    }

    /// <summary>
    /// 为「自动启动 + 管理员权限」取得一次性授权。
    ///
    /// 授权与当前目标程序的路径、参数绑定：之后换了目标程序就要重新确认。
    /// 没选过目标程序时先不弹（等选了目标再说），避免弹一个没有对象的确认框。
    /// </summary>
    /// <param name="revertAutoLaunch">用户拒绝时，是否把「自动启动」开关退回关闭。</param>
    /// <param name="launchImmediately">授权成功后立刻启动一次（用于「连接时被跳过」的补救）。</param>
    private async Task EnsureElevatedAutoLaunchConsentAsync(
        bool revertAutoLaunch,
        bool launchImmediately = false)
    {
        var launch = _settings.Current.Launch;
        if (string.IsNullOrWhiteSpace(launch.TargetPath))
        {
            ShowNotification(
                Locale.Get("Startup.NeedTarget.Title"),
                Locale.Get("Startup.NeedTarget.Message"),
                InfoBarSeverity.Informational);
            return;
        }

        if (ElevatedLaunchConsent.IsValid(
                launch.ElevatedAutoLaunchConsent,
                launch.TargetPath,
                launch.TargetArguments))
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            Locale.Format(
                "Startup.ElevatedConfirm.Message",
                launch.TargetPath,
                string.IsNullOrWhiteSpace(launch.TargetArguments)
                    ? string.Empty
                    : Locale.Format("Startup.ElevatedConfirm.Arguments", launch.TargetArguments)),
            Locale.Get("Startup.ElevatedConfirm.Title"));

        if (!confirmed)
        {
            if (revertAutoLaunch)
            {
                AutoLaunchOnConnect = false;
            }
            else
            {
                RunAsAdministrator = false;
            }

            return;
        }

        launch.ElevatedAutoLaunchConsent = ElevatedLaunchConsent.Issue(
            launch.TargetPath,
            launch.TargetArguments);
        _settings.SaveSoon();
        AppLog.Info($"已授权「自动以管理员权限启动」：{launch.TargetPath}");

        if (launchImmediately)
        {
            // 这次连接时因为没授权被跳过了：现在补上，立刻启动一次，
            // 用户不必再去菜单里点「立即在分身中启动」。
            try
            {
                await _childSessionService.LaunchTargetAsync();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.ComponentModel.Win32Exception
                                                  or ObjectDisposedException)
            {
                AppLog.Warn("授权后立刻启动目标程序失败。", exception);
                ShowNotification(
                    Locale.Get("Startup.LaunchFailed.Title"),
                    Locale.Format("Startup.LaunchFailed.Message", exception.GetBaseException().Message),
                    InfoBarSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// 启动桌面分身。启动前依次检查环境阻塞项与风险项，用户选择不继续则中止。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        // 记下代际要放在最前面：下面这个说明框不是模态的，用户完全可能在它开着的时候
        // 从诊断面板关掉桌面分身，随后「同意」接着把会话又建回来。
        var startGeneration = _childSessionService.StartGeneration;

        if (!await EnsureSystemChangeDisclosedAsync())
        {
            return;
        }

        if (_childSessionService.ConnectedState != 1)
        {
            // 预检要读上千个计划任务 XML，放后台跑：这里在 UI 线程上，
            // 同步调用会直接把界面卡住。
            await Task.Run(() => _childSessionService.RefreshEnvironmentCheck());

            // 服务只是「没启动」时先替用户拉一次：状态栏已经写着「TermService：未启动」，
            // 用户点「启动」即视为同意。被禁用或启动失败的情况不在这里处理，
            // 照常走下面的拦截提示，由用户到诊断面板里修。
            if (_childSessionService.HasBlockingEnvironmentIssue
                && await Task.Run(() => _childSessionService.TryStartTermServiceIfStopped(out _)))
            {
                await Task.Run(() => _childSessionService.RefreshEnvironmentCheck());
                _childSessionService.RefreshState();
            }

            // 阻塞项（例如缺少 RDP 主机）直接拒绝，避免用户白等 60 秒超时。
            // 不再弹一个只有「我知道了」的框——那是个死胡同；直接把诊断面板推到用户面前，
            // 他下一步要做的事（修复）就在那里。
            if (_childSessionService.HasBlockingEnvironmentIssue)
            {
                ShowDiagnostics(showAll: false, startupFailure: true);
                return;
            }

            if (_childSessionService.HasEnvironmentIssue)
            {
                // 风险项只是提示，不应该挡住本来可以连上的用户。
                var confirmed = await Dialogs.ConfirmAsync(
                    _childSessionService.EnvironmentIssueSummary
                    + Locale.Get("Startup.IssueConfirm.Suffix"),
                    Locale.Get("Startup.IssueConfirm.Title"));
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

            // 带上代际：等待期间被注销的启动由服务侧直接放弃。
            await _childSessionService.StartAsync(startGeneration);
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
            Locale.Get("Startup.FirstRun.Message"),
            Locale.Get("Startup.FirstRun.Title"));
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
            Title = Locale.Get("Dialog.SelectProgram.Title"),
            Filter = Locale.Get("Dialog.SelectProgram.Filter"),
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
                Locale.Get("Notify.TargetSelected.Title"),
                Locale.Format("Notify.TargetSelected.Message", Path.GetFileName(path)),
            InfoBarSeverity.Success);

        // 换了目标程序，之前那份「自动以管理员权限启动」的授权就作废了
        // （它是和路径绑定的），这里重新确认一次；不确认就退化成「跳过并提示」。
        if (launch.RunAsAdministrator && launch.AutoLaunchOnConnect)
        {
            launch.ElevatedAutoLaunchConsent = null;
            _settings.SaveSoon();
            _ = EnsureElevatedAutoLaunchConsentAsync(revertAutoLaunch: false);
        }
    }

    /// <summary>
    /// 串流端被后台恢复循环成功拉起。
    /// </summary>
    private void OnStreamerRecovered(object? sender, EventArgs e)
    {
        void Notify()
        {
            ShowNotification(
                Locale.Get("Uu.Recovered.Title"),
                Locale.Get("Uu.Recovered.Message"),
                InfoBarSeverity.Success,
                TimeSpan.FromSeconds(8));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(Notify));
            return;
        }

        Notify();
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
            Locale.Format(
                "Startup.ElevatedLaunch.Message",
                launch.TargetPath,
                string.IsNullOrWhiteSpace(launch.TargetArguments)
                    ? string.Empty
                    : Locale.Format("Startup.ElevatedConfirm.Arguments", launch.TargetArguments)),
            Locale.Get("Startup.ElevatedLaunch.Title"));
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
    public string UuViewSwitchMenuHeader => UuViewSwitchEnabled
        ? Locale.Format("Menu.UuSwitch.Enabled", UuHotkeyText)
        : Locale.Get("Menu.UuSwitch.Disabled");

    /// <summary>
    /// 自动登录菜单文本。
    /// </summary>
    public string AutoLoginMenuHeader => AutoLoginEnabled
        ? Locale.Get("Menu.AutoLogin.Enabled")
        : Locale.Get("Menu.AutoLogin.Disabled");

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
        _childSessionService.DesktopSizeChanged -= OnDesktopSizeChanged;
        _globalHotkey?.Dispose();
        _globalHotkey = null;
        _sessionAgent.SwitchRequested -= OnSessionAgentSwitchRequested;
        _sessionAgent.Dispose();
        _uuViewSwitchService.StreamerRecovered -= OnStreamerRecovered;
        _uuViewSwitchService.Dispose();
    }

    /// <summary>
    /// 重启 UU 的串流服务。
    ///
    /// 用于「快速连续切换之后，手机上的 UU 显示设备离线」的情况：
    /// 切换要靠结束串流端来实现，短时间内连续结束会被 UU 服务当成连续崩溃而进入
    /// 重启退避，之后它不再拉起串流端。重启服务可以清掉这个状态。
    /// </summary>
    [RelayCommand]
    private async Task RepairUuStreamingAsync()
    {
        if (_uuSwitchInProgress)
        {
            return;
        }

        _uuSwitchInProgress = true;
        try
        {
            ShowNotification(
                Locale.Get("Uu.Repair.Title"),
                Locale.Get("Uu.Repair.Message"),
                InfoBarSeverity.Informational,
                TimeSpan.FromSeconds(30));

            var result = await _uuViewSwitchService.RestartStreamerServiceAsync(CancellationToken.None);
            AppLog.Info($"UU 串流修复：{(result.Success ? "成功" : "失败")} —— {result.Message}");
            ShowNotification(
                result.Success
                    ? Locale.Get("Uu.Repair.Done")
                    : Locale.Get("Uu.Repair.Failed"),
                result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            _uuSwitchInProgress = false;
        }
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
            // 「正在切换」要贯穿整个切换过程（断开、杀串流端、等 UU 重新挑会话，
            // 可能长达 30 秒）。之前只显示 3 秒，窗口还原进全屏时提示早就没了，
            // 用户看到的是「无任何提示直接进全屏」。
            ShowNotification(
                Locale.Get("Uu.Switching.Title"),
                Locale.Get("Uu.Switching.Message"),
                InfoBarSeverity.Informational,
                TimeSpan.FromSeconds(35));

            var result = await _uuViewSwitchService.ToggleAsync(CancellationToken.None);

            // 结果必须落日志：这次实测里切换的成败只出现在界面上，
            // 排障时只能从「有没有杀串流端」间接猜，代价太高。
            AppLog.Info(
                $"视角切换结束：{(result.Success ? "成功" : "失败")} —— {result.Message}");

            ShowNotification(
                result.Success
                    ? Locale.Get("Uu.Done.Title")
                    : Locale.Get("Uu.Failed.Title"),
                result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(8));

            // 切到「整台电脑」的收尾会把窗口最小化（分身挂后台），
            // InfoBar 在最小化的窗口里根本看不见——托盘气泡是唯一的可见通道。
            // 切回分身时窗口会还原，InfoBar 可见，气泡只是顺手多一层保险。
            if (!_childSessionService.IsDesktopVisible)
            {
                App.ShowTrayBalloon(
                    result.Success
                        ? Locale.Get("Uu.Done.Title")
                        : Locale.Get("Uu.Failed.Title"),
                    result.Message);
            }
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
                Locale.Get("Notify.UuDisabled.Title"),
                Locale.Get("Notify.UuDisabled.Message"),
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
        OnPropertyChanged(nameof(UuViewSwitchMenuHeader));

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
                Locale.Get("Notify.ResolutionMismatch.Title"),
                Locale.Format(
                    "Notify.ResolutionMismatch.Message",
                    session.DesktopWidth,
                    session.DesktopHeight,
                    screenWidth,
                    screenHeight),
            InfoBarSeverity.Warning,
            TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// 分身分辨率被改动（菜单里改的，或检测到分身内部自己改的）。
    /// 刷新分辨率菜单的勾选状态与状态栏文本。
    /// </summary>
    private void OnDesktopSizeChanged(object? sender, EventArgs e)
    {
        NotifyResolutionChanged();
    }

    /// <summary>
    /// 打开诊断与修复面板。已经打开时把它叫到前面并刷新一次。
    /// </summary>
    [RelayCommand]
    private void OpenDiagnostics() => ShowDiagnostics(showAll: false);

    /// <summary>
    /// 打开「全部检查项」视图：把当前没有触发的检查也列出来。
    ///
    /// 入口是「按住 Shift 点诊断与修复」。正常视图只列当前存在的问题，
    /// 想核对某条文案（尤其是改翻译）时根本看不到它——这个视图补上这一块。
    /// 不带 [RelayCommand]：它需要参数，由菜单项的 Click 事件直接调用。
    /// </summary>
    /// <param name="showAll">是否列出未触发的检查项。</param>
    /// <param name="startupFailure">
    /// true 表示这次是被「启动失败」带进来的：面板顶部会显示「启动失败」与下一步引导。
    /// </param>
    /// <param name="credentialHint">
    /// true 表示补一条「密码可能不正确」的运行时结论。只有连接失败后的现场才传 true。
    /// </param>
    internal void ShowDiagnostics(
        bool showAll,
        bool startupFailure = false,
        bool credentialHint = false)
    {
        if (_diagnosticsWindow is { IsLoaded: true } existing)
        {
            if (startupFailure)
            {
                existing.MarkStartupFailure();
            }

            existing.SetCredentialHint(credentialHint);
            existing.RefreshNow();
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new DiagnosticsWindow(showAll, startupFailure, credentialHint);
        window.PasswordSetupRequested += OnPasswordSetupRequested;
        window.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow = window;
        Dialogs.Show(window);
    }

    /// <summary>
    /// 打开「自动登录方式」对话框：让用户在本程序自动登录与 Windows 系统自动登录之间自己选。
    ///
    /// 已经启用本程序的自动登录时不再弹这个对话框：里面那个「启用自动登录」按钮
    /// 对着已经启用的用户是废话，用户点进来通常就是想换密码——直接复用
    /// 「设置登录密码」的弹窗。（系统自动登录有单独的菜单项，不受影响。）
    /// </summary>
    [RelayCommand]
    private async Task ShowAutoLoginOptionsAsync()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            ShowNotification(Locale.Get("AutoLogin.Unavailable.Title"), Locale.Get("AutoLogin.Unavailable.Message"), InfoBarSeverity.Warning);
            return;
        }

        if (AutoLoginEnabled)
        {
            await TrySaveAndVerifyPasswordAsync();
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
            ShowNotification(Locale.Get("AutoLogin.OpenFailed.Title"), error, InfoBarSeverity.Warning, TimeSpan.FromSeconds(7));
            return;
        }

        ShowNotification(
                Locale.Get("AutoLogin.SystemOpened.Title"),
                Locale.Get("AutoLogin.SystemOpened.Message"),
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
                Locale.Get("AutoLogin.Enabled.Title"),
                Locale.Format("AutoLogin.Enabled.Message", autoLogin?.UserName),
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
            ShowNotification(Locale.Get("AutoLogin.Unavailable.Title"), Locale.Get("AutoLogin.Unavailable.Message"), InfoBarSeverity.Warning);
            return;
        }

        if (AutoLoginEnabled)
        {
            AutoLoginEnabled = false;
            _settings.Current.Automation.AutoLoginEnabled = false;
            _settings.SaveSoon();
            ShowNotification(
                Locale.Get("AutoLogin.Disabled.Title"),
                Locale.Get("AutoLogin.Disabled.Message"),
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
                Locale.Get("AutoLogin.Cleared.Title"),
                Locale.Get("AutoLogin.Cleared.Message"),
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 弹出密码框，在**弹窗内**完成校验，验证通过才保存。
    ///
    /// 校验用 <c>LogonUser</c> 的交互式登录（LOGON32_LOGON_INTERACTIVE）：不弹系统框、
    /// 不创建进程；对微软账户会先比对本机缓存，不匹配时联网校验并回写缓存，
    /// 因此云端改密后也能直接得到真实结论，顺带把缓存修好。
    ///
    /// 失败时窗口保持打开、显示原因，密码不落盘——避免把错密码存进去覆盖原来的正确密码。
    /// </summary>
    /// <returns>验证通过并保存成功返回 true。</returns>
    private async Task<bool> TrySaveAndVerifyPasswordAsync()
    {
        var autoLogin = _childSessionService.AutoLogin;
        if (autoLogin is null)
        {
            return false;
        }

        var result = await PasswordDialog.ShowAsync(
            autoLogin.UserName,
            confirmText: null,
            autoLogin.StoredAccountNames,
            async password =>
            {
                var (check, errorCode) = await Task.Run(() =>
                {
                    var outcome = autoLogin.VerifyPassword(password, out var code);
                    return (outcome, code);
                });

                if (check != AutoLoginService.PasswordCheckResult.Valid)
                {
                    return PasswordValidationResult.Fail(BuildVerifyFailureMessage(check, errorCode));
                }

                // 只有验证通过才落盘；失败时原有密码保持不变。
                return autoLogin.SavePassword(password)
                    ? PasswordValidationResult.Ok()
                    : PasswordValidationResult.Fail(Locale.Get("AutoLogin.SaveFailed.Message"));
            });

        if (result is null)
        {
            return false;
        }

        HasAutoLoginPassword = true;

        ShowNotification(
            Locale.Get("AutoLogin.VerifyOk.Title"),
            Locale.Get("AutoLogin.VerifyOk.Message"),
            InfoBarSeverity.Success,
            TimeSpan.FromSeconds(7));
        return true;
    }

    /// <summary>
    /// 把校验失败翻译成一句可执行的话，显示在密码框下面。
    ///
    /// 1326 已用交互式登录校验过（会对微软账户联网验证），所以「密码错」与「网络不通」
    /// 会得到同一个错误码，文案把这两种可能一起说清楚，而不是断言密码错了。
    /// </summary>
    private static string BuildVerifyFailureMessage(
        AutoLoginService.PasswordCheckResult result,
        int errorCode)
    {
        return result switch
        {
            AutoLoginService.PasswordCheckResult.AccountDisabled =>
                        Locale.Get("AutoLogin.Verify.AccountDisabled"),
            AutoLoginService.PasswordCheckResult.AccountLocked =>
                        Locale.Get("AutoLogin.Verify.AccountLocked"),
            AutoLoginService.PasswordCheckResult.PasswordMustChange =>
                        Locale.Get("AutoLogin.Verify.MustChangePassword"),
            AutoLoginService.PasswordCheckResult.PasswordExpired =>
                        Locale.Get("AutoLogin.Verify.PasswordExpired"),
            AutoLoginService.PasswordCheckResult.AccountRestricted =>
                        Locale.Get("AutoLogin.Verify.AccountRestricted"),
            AutoLoginService.PasswordCheckResult.InvalidPassword =>
                        Locale.Get("AutoLogin.Verify.BadPassword"),
            _ =>
                        Locale.Format("AutoLogin.Verify.Unknown", errorCode)
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
            _sessionAgent.Uninstall();
            return;
        }

        var combination = _settings.Current.Automation.UuViewSwitchHotkey;
        if (_globalHotkey.TryRegister(_hotkeyWindow, combination, out var error))
        {
            // 分身端：Console 侧热键在焦点进分身画面后会失效，必须由分身会话里的代理接住。
            var agentReady = _sessionAgent.EnsureInstalled(combination, out var agentError);
            if (!agentReady)
            {
                AppLog.Warn($"分身端安装失败，热键在分身画面内可能不生效：{agentError}");
            }
            else
            {
                // 自启项只在下次登录时生效，用户当前就开着分身，所以立刻拉一份。
                _ = _sessionAgent.TryStartInChildSession();
            }

            if (notifySuccess)
            {
                ShowNotification(
                Locale.Get("Notify.UuEnabled.Title"),
                Locale.Format(
                    agentReady ? "Notify.UuEnabled.Message.Agent" : "Notify.UuEnabled.Message",
                    GlobalHotkeyService.Format(combination)),
                    InfoBarSeverity.Success,
                    TimeSpan.FromSeconds(7));
            }

            return;
        }

        // 注册失败就把开关退回去，避免界面显示「已启用」但按键没反应。
        AppLog.Warn($"UU 视角切换热键注册失败：{error}");
        UuViewSwitchEnabled = false;
        _settings.Current.Automation.UuViewSwitchEnabled = false;
        _settings.SaveSoon();
        _sessionAgent.Uninstall();
            ShowNotification(Locale.Get("Hotkey.Registered.Failed.Title"), error, InfoBarSeverity.Warning, TimeSpan.FromSeconds(7));
    }

    /// <summary>
    /// 分身端命中热键。和主窗口内的热键走同一条切换路径。
    /// </summary>
    private void OnSessionAgentSwitchRequested(object? sender, EventArgs e)
    {
        _ = TriggerUuViewSwitchAsync();
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
            reconnectStarted ? Locale.Get("Notify.SettingsApplying") : Locale.Get("Notify.SettingsSaved"),
                reconnectStarted
                ? Locale.Get("Notify.SystemShortcuts.Applying")
                : Locale.Get("Notify.SystemShortcuts.NextConnect"),
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsSystemShortcutsReconnectPending = false;
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            ShowNotification(
                Locale.Get("Notify.SettingsSwitchFailed.Title"),
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
            enabled ? Locale.Get("Notify.WatchMode.On") : Locale.Get("Notify.WatchMode.Off"),
            enabled
                ? Locale.Get("Notify.WatchMode.On.Message")
                : Locale.Get("Notify.WatchMode.Off.Message"),
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
            reconnectStarted ? Locale.Get("Notify.AudioApplying") : Locale.Get("Notify.AudioSaved"),
                reconnectStarted
                ? Locale.Get("Notify.Audio.Applying")
                : IsAudioMuted
                    ? Locale.Get("Notify.Audio.NextConnect.Muted")
                    : Locale.Get("Notify.Audio.NextConnect.On"),
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsAudioReconnectPending = false;
            IsAudioMuted = _childSessionService.AudioMuted;
            ShowNotification(
                Locale.Get("Notify.AudioSwitchFailed.Title"),
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
                Locale.Get("Notify.EnvCopied.Title"),
                Locale.Get("Notify.EnvCopied.Message"),
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
                Locale.Get("Diag.DisableChildSessions.Message"),
                Locale.Get("Menu.Diagnostics.DisableChildSessions"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _childSessionService.LogoffAndHideAsync();
            _childSessionService.DisableChildSessionsFeature();
            ShowNotification(
                Locale.Get("Notify.ChildSessionsOff.Title"),
                Locale.Get("Notify.ChildSessionsOff.Message"),
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
            EnsureSessionAgentRunning();
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateConnectionStatus();
            EnsureSessionAgentRunning();
        });
    }

    /// <summary>
    /// 分身连上时确保热键代理在跑。
    ///
    /// 代理是否在线由 <see cref="SessionAgentService.IsAgentOnline"/> 判定，
    /// 那里面已经含「刚拉起的 60 秒启动宽限」：分身端冷启动到第一条心跳要
    /// 7～12 秒，没有宽限就会把正在启动的代理误判成「不在线」再拉一个新的，
    /// 新代理又会杀掉旧的（自噬循环）。
    /// </summary>
    private void EnsureSessionAgentRunning()
    {
        if (!UuViewSwitchEnabled || _childSessionService.ConnectedState != 1)
        {
            return;
        }

        if (!_sessionAgent.NeedsRestart && _sessionAgent.IsAgentOnline())
        {
            return;
        }

        // 状态事件来自 500ms 轮询，不加节流会反复下发启动任务。
        var now = Environment.TickCount64;
        if (now - _lastAgentStartTicks < 10_000)
        {
            return;
        }

        _lastAgentStartTicks = now;

        // 进程还在就别再拉一个：新代理抢不到互斥体会立刻退出，除了多一条日志什么也做不到。
        if (!_sessionAgent.NeedsRestart && _sessionAgent.HasLiveAgentProcess())
        {
            AppLog.Info("分身端进程仍在（心跳停滞），跳过重启以免多开。");
            return;
        }

        AppLog.Info(_sessionAgent.NeedsRestart
            ? "分身端脚本已更新，拉起新代理替换旧的。"
            : "分身端未在运行，重新拉起。");
        _ = _sessionAgent.TryStartInChildSession();
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
                Locale.Get("Notify.RdpFailed.Title"),
                e.Message + Locale.Get("Notify.RdpFailed.Suffix"),
                    InfoBarSeverity.Error,
                    TimeSpan.FromSeconds(10));

                // 连接失败时自动弹诊断面板：RDP ActiveX 只会回报「发生内部错误」，
                // 真正的原因几乎都在环境检查里，让用户自己去菜单里找太绕。
                // 打上「启动失败」横幅并带上「密码可能不正确」的推断——两者都是这次失败的一部分。
                ShowDiagnostics(
                    showAll: false,
                    startupFailure: true,
                    credentialHint: _childSessionService.CredentialFailureSuspected);
            }));
    }

    /// <summary>
    /// 面板上「设置自动登录…」被点击：输入并验证密码，通过后启用本程序的自动登录。
    ///
    /// 「密码可能不正确」这条结论只有用户把密码重设一次才算处理掉，
    /// 所以成功之后要一并作废推断，让面板上的那条跟着消失。
    /// </summary>
    private async void OnPasswordSetupRequested(object? sender, EventArgs e)
    {
        if (!await TrySaveAndVerifyPasswordAsync())
        {
            return;
        }

        if (!AutoLoginEnabled)
        {
            EnableAppAutoLogin();
        }

        _childSessionService.ClearCredentialFailureSuspected();
        _diagnosticsWindow?.SetCredentialHint(false);
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
                Locale.Get("Notify.Applied.Title"),
                SendSystemShortcutsToRemote
                    ? Locale.Get("Notify.Applied.ToChild")
                    : Locale.Get("Notify.Applied.ToLocal"),
                    InfoBarSeverity.Success);
            }));
    }

    private void OnAutoLaunchSkipped(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                var launch = _settings.Current.Launch;

                // 用户其实早就把两个开关都勾上了（比如从旧版本带过来的设置），
                // 但「确认一次」这个动作没发生过——这里当场问，别让它一直跳过。
                if (launch.RunAsAdministrator
                    && launch.AutoLaunchOnConnect
                    && !string.IsNullOrWhiteSpace(launch.TargetPath)
                    && !ElevatedLaunchConsent.IsValid(
                        launch.ElevatedAutoLaunchConsent,
                        launch.TargetPath,
                        launch.TargetArguments))
                {
                    _ = EnsureElevatedAutoLaunchConsentAsync(
                        revertAutoLaunch: false,
                        launchImmediately: true);
                    return;
                }

                ShowNotification(
                Locale.Get("Startup.AutoLaunchSkipped.Title"),
                Locale.Get("Startup.AutoLaunchSkipped.Message"),
                    InfoBarSeverity.Warning,
                    TimeSpan.FromSeconds(12));
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
            IsAudioMuted ? Locale.Get("Notify.Muted.Title") : Locale.Get("Notify.SoundOn.Title"),
                    IsAudioMuted
                ? Locale.Get("Notify.Muted.Message")
                : Locale.Get("Notify.SoundOn.Message"),
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

    /// <summary>
    /// 通知是否仍在有效期内（超时定时器还没走完）。
    ///
    /// 窗口隐藏时通知条会被临时关掉，而它是两向绑定的，一关就把 IsNotificationOpen
    /// 写回 false。所以恢复窗口时不能看那个值，只能看定时器还在不在跑。
    /// </summary>
    public bool IsNotificationActive => _notificationTimer.IsEnabled;

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

        // 连上了说明凭据是对的：这次失败的推断作废，面板上那条提示跟着撤掉。
        // 只在确实还挂着推断时才动手，避免每轮轮询都写一次。
        if (IsRdpConnected && _childSessionService.CredentialFailureSuspected)
        {
            _childSessionService.ClearCredentialFailureSuspected();
            _diagnosticsWindow?.SetCredentialHint(false);
        }

        if (IsSessionClosing)
        {
            ConnectionStatusBrush = Brushes.Orange;
            ConnectionStatusTitle = Locale.Get("Main.Status.Closing");
            ConnectionStatusDescription = Locale.Get("Main.Status.Closing.Description");
            RdpStatusText = connectedState == 0 ? Locale.Get("Main.Rdp.Disconnected") : Locale.Get("Main.Rdp.Disconnecting");
            ChildSessionStatusText = childSessionId is null
                ? Locale.Get("Main.Status.Closing.Confirming")
                : Locale.Format("Main.Status.Closing.LoggingOff", childSessionId.Value);
            ConnectionStatusToolTip = Locale.Get("Main.Status.Closing.ToolTip");
            return;
        }

        if (IsRdpConnected)
        {
            ConnectionStatusBrush = Brushes.LimeGreen;
            ConnectionStatusTitle = Locale.Get("Main.Status.Connected");
            ConnectionStatusDescription = Locale.Get("Main.Status.Connected.Description");
        }
        else if (connectedState == 2)
        {
            ConnectionStatusBrush = childSessionId is null ? Brushes.Red : Brushes.DodgerBlue;
            ConnectionStatusTitle = Locale.Get("Main.Status.Connecting");
            ConnectionStatusDescription = Locale.Get("Main.Status.Connecting.Description");
        }
        else if (childSessionId is not null)
        {
            ConnectionStatusBrush = Brushes.DodgerBlue;
            ConnectionStatusTitle = Locale.Get("Main.Status.Started");
            ConnectionStatusDescription = Locale.Get("Main.Status.Started.Description");
        }
        else
        {
            ConnectionStatusBrush = Brushes.Red;
            ConnectionStatusTitle = Locale.Get("Main.Status.NotStarted");
            ConnectionStatusDescription =
            Locale.Get("Main.Status.Description");
        }

        RdpStatusText = connectedState switch
        {
                0 => Locale.Get("Main.Rdp.NotConnected"),
                1 => Locale.Get("Main.Rdp.Connected"),
                2 => Locale.Get("Main.Rdp.Connecting"),
                _ => Locale.Format("Main.Rdp.Unknown", connectedState)
        };
        ChildSessionStatusText = childSessionId is null
                ? Locale.Get("Main.Session.None")
                : Locale.Format("Main.Session.Number", childSessionId.Value);
        ConnectionStatusToolTip = _childSessionService.StatusText;
    }

    private static string BuildTargetProgramText(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? Locale.Get("Menu.TargetProgram.None")
            : Path.GetFileName(path);
    }

    private static void ShowError(Exception exception)
    {
        var actualException = exception.GetBaseException();
        var suggestion = actualException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                ? Locale.Get("Error.AccessDenied.Suffix")
            : string.Empty;

        AppLog.Error("界面操作失败。", actualException);
        Dialogs.ShowError(
            $"{actualException.Message}{suggestion}",
                Locale.Get("Main.Title"));
    }
}
