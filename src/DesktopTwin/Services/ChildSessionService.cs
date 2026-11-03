using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Models;
using DesktopTwin.View;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace DesktopTwin.Services;

/// <summary>
/// 桌面分身的编排层：负责会话生命周期、RDP 连接状态、失败重试与界面状态文本。
///
/// 分层说明：本类直接持有 <see cref="MainWindow"/> 的引用。这是一个单窗口应用，
/// 为「一个窗口 + 一个会话」的强绑定关系引入额外抽象只会增加阅读成本，
/// 因此这里选择务实做法：服务负责状态与流程，窗口只负责显示。
/// </summary>
public sealed class ChildSessionService : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialConnectionRetryCount = 3;
    private const int RdpSettingsReconnectRetryCount = 2;
    private const int ErrorTimeout = 1460;

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private MainWindow? _desktopWindow;
    private bool _autoLaunchPending;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;
    private RdpSettingChange _pendingRdpSettingChanges;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _statusTickInProgress;
    private bool _disposed;
    private string? _lastOperationMessage;
    private System.Collections.Generic.IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _environmentIssues = [];

    /// <summary>
    /// 界面需要刷新的状态发生变化。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// RDP 连接失败或被意外断开。
    /// </summary>
    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    /// <summary>
    /// 「系统组合键发送位置」切换后的自动重连已完成。
    /// </summary>
    public event EventHandler? SystemShortcutsReconnectCompleted;

    /// <summary>
    /// 音频静音切换后的自动重连已完成。
    /// </summary>
    public event EventHandler? AudioReconnectCompleted;

    /// <summary>
    /// 分身窗口实例。窗口在首次显示或首次连接时创建，未创建时为 null。
    /// </summary>
    public MainWindow? Window => _desktopWindow;

    /// <summary>
    /// 状态栏文本。
    /// </summary>
    public string StatusText { get; private set; } = "桌面分身尚未启动";

    /// <summary>
    /// 分身窗口当前是否可见。
    /// </summary>
    public bool IsDesktopVisible => _desktopWindow?.IsVisible == true;

    /// <summary>
    /// RDP ActiveX 的连接状态：0 未连接、1 已连接、2 正在连接。
    /// </summary>
    public int ConnectedState { get; private set; }

    /// <summary>
    /// 当前分身会话号，未创建时为 null。
    /// </summary>
    public uint? ChildSessionId { get; private set; }

    /// <summary>
    /// 系统组合键是否发送到分身。
    /// </summary>
    public bool SendSystemShortcutsToRemote { get; private set; }

    /// <summary>
    /// 窗口是否置顶。
    /// </summary>
    public bool TopmostEnabled => _settings.Current.Session.Topmost;

    /// <summary>
    /// 画面是否自适应窗口。
    /// </summary>
    public bool SmartSizingEnabled => _settings.Current.Session.SmartSizing;

    /// <summary>
    /// 是否保持宽高比。
    /// </summary>
    public bool KeepAspectRatio => _settings.Current.Session.KeepAspectRatio;

    /// <summary>
    /// 分身声音是否关闭。
    /// </summary>
    public bool AudioMuted => _settings.Current.Session.AudioMuted;

    /// <summary>
    /// 是否处于观看模式。
    /// </summary>
    public bool WatchModeEnabled => _settings.Current.Session.WatchMode;

    /// <summary>
    /// 分身桌面的逻辑分辨率。
    /// </summary>
    public DrawingSize DesktopSize => new(
        _settings.Current.Session.DesktopWidth,
        _settings.Current.Session.DesktopHeight);

    /// <summary>
    /// 普通窗口模式的记忆位置。
    /// </summary>
    public WindowPosition? NormalWindowPosition
    {
        get => _settings.Current.Window.NormalPosition;
        set
        {
            _settings.Current.Window.NormalPosition = value;
            _settings.SaveSoon();
        }
    }

    /// <summary>
    /// 小窗模式的记忆位置。
    /// </summary>
    public WindowPosition? SmallWindowPosition
    {
        get => _settings.Current.Window.SmallPosition;
        set
        {
            _settings.Current.Window.SmallPosition = value;
            _settings.SaveSoon();
        }
    }

    /// <summary>
    /// 系统是否已安装并启用 RDP Wrapper。它与桌面分身功能不兼容，界面会在启动前据此提示用户。
    /// </summary>
    public static bool IsRdpWrapperEnabled()
    {
        return ChildSessionNativeMethods.IsRdpWrapperEnabled();
    }

    /// <summary>
    /// 最近一次启动前环境预检是否发现了可能影响桌面分身的环境设置。
    /// </summary>
    public bool HasEnvironmentIssue => _environmentIssues.Count > 0;

    /// <summary>
    /// 环境预检中是否存在必然失败的阻塞项。
    /// </summary>
    public bool HasBlockingEnvironmentIssue =>
        ChildSessionEnvironmentCheck.HasBlockingIssue(_environmentIssues);

    /// <summary>
    /// 最近一次启动前环境预检的结论文本，供界面提示与问题排查使用。
    /// </summary>
    public string EnvironmentIssueSummary =>
        ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues);

    /// <summary>
    /// 创建服务。构造时立即刷新一次状态，界面随后可以安全读取。
    /// </summary>
    public ChildSessionService(SettingsService settings)
    {
        _settings = settings;
        SendSystemShortcutsToRemote = settings.Current.Session.SendSystemShortcutsToRemote;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
        RefreshState();
    }

    /// <summary>
    /// 当前是否存在分身会话。
    /// </summary>
    public bool HasActiveChildSession()
    {
        RefreshState();
        return ChildSessionId is not null;
    }

    /// <summary>
    /// 启动桌面分身：先做一次环境预检，确保 Child Sessions 已启用，然后建立 RDP 连接并等待登录完成。
    /// 已经处于已连接状态时直接返回。
    /// </summary>
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        RefreshEnvironmentCheck();
        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1)
        {
            return;
        }

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;

            try
            {
                ConnectCore("正在启动桌面分身");
            }
            catch
            {
                _autoLaunchPending = false;
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                throw;
            }
        }

        try
        {
            await completionSource.Task.WaitAsync(
                ConnectionTimeout,
                _disposeCancellationTokenSource.Token);
        }
        catch (TimeoutException)
        {
            if (!completionSource.Task.IsCompleted)
            {
                _autoLaunchPending = false;
                _initialConnectionRetriesRemaining = 0;
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                TryDisconnectRdpHost();
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    /// <summary>
    /// 显示分身窗口。窗口不存在时创建。
    /// </summary>
    public void ShowWindow()
    {
        ThrowIfDisposed();
        ShowDesktopWindow(EnsureDesktopWindow());
        RefreshState();
    }

    /// <summary>
    /// 隐藏分身窗口，RDP 连接保持不变。
    /// </summary>
    public void HideWindow()
    {
        ThrowIfDisposed();
        _desktopWindow?.Hide();
        RefreshState("已隐藏桌面分身窗口，RDP 连接保持不变");
    }

    /// <summary>
    /// 向分身发送 Win+D 显示桌面。
    /// </summary>
    public void ShowChildSessionDesktop()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendShowDesktopShortcut();
        RefreshState("已向桌面分身发送 Win+D");
    }

    /// <summary>
    /// 向分身发送 Win+Tab 打开任务视图。
    /// </summary>
    public void ShowChildSessionTaskView()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendTaskViewShortcut();
        RefreshState("已向桌面分身发送 Win+Tab");
    }

    /// <summary>
    /// 切换画面自适应 / 1:1。
    /// </summary>
    public void SetSmartSizing(bool enabled)
    {
        ThrowIfDisposed();
        EnsureDesktopWindow().RdpHost.SetSmartSizing(enabled);
        _settings.Current.Session.SmartSizing = enabled;
        _settings.SaveSoon();
        RefreshState(enabled ? "窗口显示模式已切换为自适应" : "窗口显示模式已切换为 1:1");
    }

    /// <summary>
    /// 切换是否保持宽高比。
    /// </summary>
    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.KeepAspectRatio = enabled;
        _settings.SaveSoon();
        RefreshState(enabled ? "窗口将保持 16:9 宽高比" : "窗口不再保持宽高比");
    }

    /// <summary>
    /// 切换窗口置顶。
    /// </summary>
    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.Topmost = enabled;
        _settings.SaveSoon();
        RefreshState(enabled ? "窗口已置顶" : "窗口已取消置顶");
    }

    /// <summary>
    /// 切换系统组合键的发送位置。需要重连 RDP 才生效。
    /// </summary>
    /// <returns>是否触发了自动重连。</returns>
    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        SendSystemShortcutsToRemote = enabled;
        _settings.Current.Session.SendSystemShortcutsToRemote = enabled;
        _settings.SaveSoon();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled ? "桌面分身" : "本机";
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.SystemShortcuts,
            $"系统组合键已改为在{target}生效");
    }

    /// <summary>
    /// 切换分身声音。需要重连 RDP 才生效。
    /// </summary>
    /// <returns>是否触发了自动重连。</returns>
    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (AudioMuted == muted)
        {
            return false;
        }

        _settings.Current.Session.AudioMuted = muted;
        _settings.SaveSoon();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? "分身声音已关闭" : "分身声音已开启");
    }

    /// <summary>
    /// 切换观看模式：开启后本机键鼠不会进入分身。
    /// </summary>
    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.WatchMode = enabled;
        _settings.SaveSoon();
        _desktopWindow?.RdpHost.SetInputBlocked(enabled);
        RefreshState(enabled
            ? "已进入观看模式，本机鼠标和键盘不会进入桌面分身"
            : "已退出观看模式，可以操作桌面分身");
    }

    /// <summary>
    /// 切换分身桌面分辨率。需要重连 RDP 才生效。
    /// </summary>
    public void SetDesktopSize(int width, int height)
    {
        ThrowIfDisposed();
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "分辨率必须是正整数。");
        }

        _settings.Current.Session.DesktopWidth = width;
        _settings.Current.Session.DesktopHeight = height;
        _settings.SaveSoon();

        var window = EnsureDesktopWindow();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState($"分辨率已设为 {width} × {height}，将在下次连接后生效");
            return;
        }

        window.RdpHost.ReconnectToChildSession(new DrawingSize(width, height));
        RefreshState($"分辨率已设为 {width} × {height}，正在自动重新连接 RDP");
    }

    /// <summary>
    /// 在分身中启动当前配置的目标程序。
    /// </summary>
    public Task LaunchTargetAsync()
    {
        ThrowIfDisposed();
        var launch = _settings.Current.Launch;
        if (string.IsNullOrWhiteSpace(launch.TargetPath))
        {
            throw new InvalidOperationException("还没有选择要在桌面分身中启动的程序。");
        }

        return LaunchProgramCoreAsync(
            launch.TargetPath,
            launch.TargetArguments,
            launch.RunAsAdministrator,
            isAutomatic: false);
    }

    /// <summary>
    /// 在分身中启动指定程序。
    /// </summary>
    public Task LaunchProgramAsync(string executablePath, bool runAsAdministrator)
    {
        ThrowIfDisposed();
        return LaunchProgramCoreAsync(executablePath, string.Empty, runAsAdministrator, isAutomatic: false);
    }

    /// <summary>
    /// 断开 RDP、注销分身会话并隐藏窗口。
    /// </summary>
    /// <param name="expectedSessionId">需要注销的预期会话标识；为空时注销当前分身会话。</param>
    public async Task LogoffAndHideAsync(uint? expectedSessionId = null)
    {
        ThrowIfDisposed();

        await _launchSemaphore.WaitAsync();
        try
        {
            var activeSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            if (expectedSessionId is { } expected
                && activeSessionId is { } active
                && active != expected)
            {
                throw new InvalidOperationException(
                    $"桌面分身会话已从 {expected} 变更为 {active}，已取消注销操作。");
            }

            // 完成目标会话校验后再取消连接和重试状态，避免旧菜单项影响刚创建的新会话。
            _autoLaunchPending = false;
            _initialConnectionRetriesRemaining = 0;
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _connectionAttemptCompletionSource?.TrySetResult(false);

            TryDisconnectRdpHost();
            RefreshState("正在断开 RDP 并注销桌面分身会话");

            var terminatedSessionId = await Task.Run(() =>
                expectedSessionId is { } targetSessionId
                    ? ChildSessionNativeMethods.TerminateChildSession(targetSessionId)
                    : ChildSessionNativeMethods.TerminateChildSession());
            _desktopWindow?.Hide();
            RefreshState(terminatedSessionId is null
                ? "当前没有分身会话，窗口已隐藏"
                : $"分身会话 {terminatedSessionId.Value} 已注销，窗口已隐藏");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>
    /// 关闭 Windows 的 Child Sessions 功能。属于高级操作，界面会二次确认。
    /// </summary>
    public void DisableChildSessionsFeature()
    {
        ThrowIfDisposed();
        ChildSessionNativeMethods.DisableChildSessions();
        RefreshState("已关闭 Windows Child Sessions 功能");
    }

    /// <summary>
    /// 刷新状态文本与连接状态。
    /// </summary>
    /// <param name="operationMessage">覆盖显示的最近操作说明。</param>
    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _desktopWindow?.RdpHost.ConnectedState ?? 0;

            var connectionText = ConnectedState switch
            {
                0 => "未连接",
                1 => "已连接",
                2 => "正在连接",
                _ => $"未知连接状态 {ConnectedState}"
            };
            var sessionText = ChildSessionId?.ToString() ?? "无";
            var mainText = _lastOperationMessage ?? connectionText;

            StatusText =
                $"{mainText} | RDP：{connectionText} | 分身会话：{sessionText} | 功能已启用：{enabled}";
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 收集 Windows 环境结论，供界面在启动前提示用户，也供失败诊断使用。
    /// </summary>
    public void RefreshEnvironmentCheck()
    {
        ThrowIfDisposed();
        _environmentIssues = ChildSessionEnvironmentCheck.Collect();

        foreach (var issue in _environmentIssues)
        {
            AppLog.Warn($"环境预检：{issue.Title}。{issue.Message}");
        }
    }

    /// <summary>
    /// 释放资源并注销分身会话。应用退出时调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _autoLaunchPending = false;
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        if (_desktopWindow is not null)
        {
            _desktopWindow.IsVisibleChanged -= OnDesktopWindowVisibilityChanged;
            _desktopWindow.RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopWindow.RdpHost.LoginCompleted -= OnRdpLoginCompleted;
            TryDisconnectRdpHost();
        }

        try
        {
            // 应用正在退出，注销失败不应阻止主程序关闭；但会话残留会占着第二个桌面，必须尝试。
            _ = ChildSessionNativeMethods.TerminateChildSession(wait: false);
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            AppLog.Warn("退出时注销分身会话失败。", exception);
        }

        if (_desktopWindow is not null)
        {
            _desktopWindow.AllowClose = true;
            _desktopWindow.Close();
            _desktopWindow = null;
        }

        _launchSemaphore.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var existingSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);

        if (window.RdpHost.ConnectedState == 0)
        {
            // 会话是新建的才需要自动拉起目标程序；接管已有会话时不能替用户再开一份。
            _autoLaunchPending = existingSessionId is null
                                 && _settings.Current.Launch.AutoLaunchOnConnect
                                 && !string.IsNullOrWhiteSpace(_settings.Current.Launch.TargetPath);
            _initialConnectionRetriesRemaining = existingSessionId is null
                ? InitialConnectionRetryCount
                : 0;
            window.RdpHost.ConnectToChildSession(DesktopSize);
        }

        RefreshState(operationMessage);
    }

    private MainWindow EnsureDesktopWindow()
    {
        if (_desktopWindow is not null)
        {
            return _desktopWindow;
        }

        _desktopWindow = new MainWindow(this, _settings);
        _desktopWindow.IsVisibleChanged += OnDesktopWindowVisibilityChanged;
        _desktopWindow.RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopWindow.RdpHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopWindow.RdpHost.SetSmartSizing(_settings.Current.Session.SmartSizing);
        _desktopWindow.RdpHost.SetSendSystemShortcutsToRemote(SendSystemShortcutsToRemote);
        _desktopWindow.RdpHost.SetAudioMuted(AudioMuted);
        return _desktopWindow;
    }

    private static void ShowDesktopWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
        }
    }

    private async Task LaunchProgramCoreAsync(
        string executablePath,
        string arguments,
        bool runAsAdministrator,
        bool isAutomatic)
    {
        await _launchSemaphore.WaitAsync();
        try
        {
            var childSessionId = GetRequiredChildSessionId();

            // 目标程序可能读取同一个程序目录下的配置，启动前先把设置刷到磁盘。
            _settings.Flush();

            var fileName = Path.GetFileName(executablePath);
            RefreshState(isAutomatic
                ? $"分身已就绪，正在自动启动 {fileName}"
                : $"正在启动 {fileName}");
            await ChildSessionProcessLauncher.LaunchAsync(
                childSessionId,
                executablePath,
                arguments,
                runAsAdministrator);
            RefreshState(
                $"已在分身（会话 {childSessionId}）中启动 {fileName}"
                + (runAsAdministrator ? "（管理员权限）" : string.Empty));
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>
    /// 取当前分身会话 ID；没有可用会话时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    private uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException("当前没有可用的分身会话，请先启动桌面分身。");
        }

        return childSessionId.Value;
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    private void OnDesktopWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
        }

        if (_autoLaunchPending
            && _initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    // RDP 报告连接成功后稍作等待，让新分身会话的桌面初始化完成。
    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        _desktopWindow?.RdpHost.SetInputBlocked(WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState("自动重新连接完成，RDP 设置已生效");

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState("桌面分身登录初始化完成");
        }

        if (!_autoLaunchPending)
        {
            return;
        }

        _autoLaunchPending = false;
        await Task.Yield();
        if (_disposed)
        {
            return;
        }

        try
        {
            RefreshState();
            if (ConnectedState == 1 && ChildSessionId is not null)
            {
                var launch = _settings.Current.Launch;
                if (!string.IsNullOrWhiteSpace(launch.TargetPath))
                {
                    await LaunchProgramCoreAsync(
                        launch.TargetPath,
                        launch.TargetArguments,
                        launch.RunAsAdministrator,
                        isAutomatic: true);
                }
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState($"自动启动目标程序失败：{exception.GetBaseException().Message}");
        }
    }

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);

        AppLog.Warn(
            $"RDP 设置切换后的自动重连失败，将在 {retryDelay.TotalSeconds:0} 秒后进行第 {retryNumber} 次重试。"
            + $"错误：{firstFailure.Message}，错误代码：{firstFailure.ErrorCode}，"
            + $"扩展错误代码：{firstFailure.ExtendedErrorCode}");
        RefreshState(
            $"RDP 自动重连暂未成功，{retryDelay.TotalSeconds:0} 秒后重试"
            + $"（{retryNumber}/{RdpSettingsReconnectRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(
                $"正在重试 RDP 自动连接（{retryNumber}/{RdpSettingsReconnectRetryCount}）");
            _rdpSettingsReconnectRetryInProgress = false;
            EnsureDesktopWindow().RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"RDP 自动重新连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _rdpSettingsReconnectRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = InitialConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << (retryNumber - 1));

        AppLog.Warn(
            $"分身首次初始化连接失败，将在 {retryDelay.TotalSeconds:0} 秒后进行第 {retryNumber} 次重试。"
            + $"错误：{firstFailure.Message}，错误代码：{firstFailure.ErrorCode}，"
            + $"扩展错误代码：{firstFailure.ExtendedErrorCode}");
        RefreshState(
            $"分身首次初始化尚未完成，{retryDelay.TotalSeconds:0} 秒后自动重试"
            + $"（{retryNumber}/{InitialConnectionRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || !_autoLaunchPending)
            {
                return;
            }

            RefreshState();
            if (_desktopWindow is null)
            {
                return;
            }

            RefreshState(
                $"正在重试分身连接（{retryNumber}/{InitialConnectionRetryCount}）");
            _connectionRetryInProgress = false;
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            // 否则这里直接退出会丢失已捕获的真实错误，最终只剩外层连接超时。
            _desktopWindow.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"重试分身连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _connectionRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    /// <summary>
    /// 连接失败的统一收尾：「重试耗尽」与「连接超时」两条路径都经由这里附加诊断信息，
    /// 再更新状态并通知订阅者。
    /// </summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        // 统一在这里补充诊断，让「重试耗尽」和「连接超时」两条失败路径给出同样的信息。
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _autoLaunchPending = false;
        _initialConnectionRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);
        AppLog.Error(
            $"分身 RDP 连接失败：{diagnosed.Message}，错误代码：{diagnosed.ErrorCode}，"
            + $"扩展错误代码：{diagnosed.ExtendedErrorCode}");
        RefreshState(diagnosed.Message);
        ConnectionFailed?.Invoke(this, diagnosed);
    }

    /// <summary>
    /// 构造连接超时的失败信息，并附上 RDP ActiveX 最后报告的失败原因（如果有）。
    /// </summary>
    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage =
            $"分身连接及登录初始化未能在 {ConnectionTimeout.TotalSeconds:0} 秒内完成。";
        var lastDiagnostic =
            _desktopWindow?.RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                $"{timeoutMessage}\n\nRDP ActiveX 未报告更具体的失败原因。",
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            $"{timeoutMessage}\n\nRDP ActiveX 最后报告：\n{lastDiagnostic.Message}",
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    /// <summary>
    /// 连接失败后的补充诊断：报告是否存在分身会话，并附上环境预检结论。
    /// RDP ActiveX 自身只回报「发生内部错误」和断开原因，单独看这些信息无法定位问题。
    /// </summary>
    private string BuildFailureDiagnosis()
    {
        var builder = new StringBuilder();

        if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(
                "诊断：当前没有可用的分身会话，说明 Windows 未能建立 RDP 会话。"
                + "该状态本身无法确定原因——系统无法创建会话与凭据未通过验证都会得到这个结果，"
                + "请结合上面的 RDP ActiveX 错误信息一并判断。");
        }

        if (_environmentIssues.Count > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append(ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 尝试断开 RDP 主机连接。ActiveX 正在自行断开时可能返回 COM 错误，忽略后仍继续注销会话。
    /// </summary>
    private void TryDisconnectRdpHost()
    {
        try
        {
            _desktopWindow?.RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，仍可继续注销分身会话。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or FileNotFoundException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool ReconnectForRdpSettingChange(
        MainWindow window,
        RdpSettingChange settingChange,
        string operationMessage)
    {
        RefreshState();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState($"{operationMessage}，将在下次 RDP 连接后应用");
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        try
        {
            window.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch
        {
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState($"{operationMessage}，正在自动重新连接 RDP");
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2
    }
}
