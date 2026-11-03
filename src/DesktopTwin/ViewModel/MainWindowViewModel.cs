using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private bool _startRequested;

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

        _notificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _notificationTimer.Tick += OnNotificationTimerTick;

        _childSessionService.StateChanged += OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed += OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted += OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted += OnAudioReconnectCompleted;
        UpdateConnectionStatus();
    }

    /// <summary>
    /// 窗口是否置顶。
    /// </summary>
    public bool IsTopmostForWindow => IsTopmost;

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
        OnPropertyChanged(nameof(IsTopmostForWindow));
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

        OnPropertyChanged(nameof(IsDefaultResolutionSelected));
        OnPropertyChanged(nameof(IsQuadHdResolutionSelected));
        OnPropertyChanged(nameof(IsHdPlusResolutionSelected));
        OnPropertyChanged(nameof(IsHdResolutionSelected));
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
        Execute(() => Process.Start(new ProcessStartInfo(AppLog.LogDirectory)
        {
            UseShellExecute = true
        }));
    }

    /// <summary>
    /// 打开设置文件所在目录。
    /// </summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        Execute(() => Process.Start(new ProcessStartInfo(_settings.SettingsDirectory)
        {
            UseShellExecute = true
        }));
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
                    e.Message,
                    InfoBarSeverity.Error,
                    TimeSpan.FromSeconds(10));
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
