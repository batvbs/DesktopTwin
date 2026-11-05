using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Services;
using DesktopTwin.ViewModel;
using Wpf.Ui.Controls;

namespace DesktopTwin.View;

/// <summary>
/// 桌面分身主窗口：标题栏放控制入口，内容区承载 RDP 画面。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private const double FullscreenBarAutoHideSeconds = 10;

    /// <summary>
    /// 鼠标进入窗口顶部这个高度（DIP）内就唤出工具栏。
    /// </summary>
    private const double FullscreenBarHotZone = 6;

    private readonly ChildSessionService _childSessionService;
    private readonly SettingsService _settings;
    private bool _closeRequestInProgress;

    private bool _isFullscreen;
    private bool _suppressStateChange;
    private bool _barPinned;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen = WindowStyle.SingleBorderWindow;
    private ResizeMode _resizeBeforeFullscreen = ResizeMode.CanResize;
    private bool _smartSizingBeforeFullscreen;
    private DispatcherTimer? _barAutoHideTimer;
    private DispatcherTimer? _cursorWatchTimer;
    private DispatcherTimer? _remoteSizeWatchTimer;
    private HwndSource? _windowSource;

    /// <summary>
    /// 承载 RDP ActiveX 的宿主控件。
    /// </summary>
    internal RdpActiveXHost RdpHost { get; } = new();

    /// <summary>
    /// 是否允许直接关闭窗口（应用退出或服务主动关闭时为 true）。
    /// </summary>
    internal bool AllowClose { get; set; }

    /// <summary>
    /// 创建主窗口。
    /// </summary>
    public MainWindow(ChildSessionService childSessionService, SettingsService settings)
        : this(childSessionService, settings, new MainWindowViewModel(childSessionService, settings))
    {
    }

    /// <summary>
    /// 创建主窗口并绑定指定 ViewModel。
    /// </summary>
    public MainWindow(
        ChildSessionService childSessionService,
        SettingsService settings,
        MainWindowViewModel viewModel)
    {
        _childSessionService = childSessionService;
        _settings = settings;
        _barPinned = settings.Current.Window.FullscreenBarPinned;
        DataContext = viewModel;
        InitializeComponent();
        RdpFormsHost.Child = RdpHost;

        // Topmost 由 XAML 里的 OneWay 绑定驱动（MainWindow.xaml:20），
        // ViewModel 构造时已用设置里的值初始化 IsTopmost。
        // 这里绝不能再给 Topmost 赋值：对已有绑定的依赖属性调用 SetValue 会清掉绑定，
        // 置顶按钮从此失效（要保留绑定只能用 SetCurrentValue）。
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

        // 全局热键需要窗口句柄，构造阶段还拿不到，等 Loaded 之后再注册。
        Loaded += OnWindowLoaded;

        // 点最大化按钮 → 进入全屏，而不是普通的「最大化到工作区」。
        StateChanged += OnWindowStateChanged;

        // 全屏下窗口失焦（切到别的程序、弹出凭据框等）时把浮层收起；
        // 固定状态下不动，那是用户明确要求常驻的。
        Deactivated += OnWindowDeactivated;

        // 分身分辨率被改时，全屏下的「1:1 / 自适应」判定前提也变了。
        _childSessionService.DesktopSizeChanged += OnDesktopSizeChanged;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭时先收浮层：它是独立窗口，不跟着主窗口一起消失。
        HideFullscreenBar();

        if (AllowClose || Dispatcher.HasShutdownStarted)
        {
            base.OnClosing(e);
            return;
        }

        // 关闭动作要等用户确认，而 Closing 是同步事件，因此先取消关闭再异步询问。
        e.Cancel = true;
        base.OnClosing(e);

        if (_closeRequestInProgress)
        {
            return;
        }

        _ = ConfirmAndLogoffAsync();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Detach();
        }

        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnWindowMessage);
            _windowSource = null;
        }

        _childSessionService.DesktopSizeChanged -= OnDesktopSizeChanged;

        RdpFormsHost.Child = null;
        RdpHost.Dispose();
        base.OnClosed(e);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 只需要句柄，重复触发（窗口重新显示）时重复调用也无副作用：
        // 内部会先注销再按当前设置重新注册。
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.AttachWindow(this);
        }

        // 监听 WM_DISPLAYCHANGE：全屏时用户改了分辨率或换了显示器，
        // 窗口要重新贴合新尺寸，缩放模式也要重新判定。
        if (_windowSource is null)
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(OnWindowMessage);
        }
    }

    /// <summary>
    /// 用户点最大化按钮（或双击标题栏、Win+Up）时进入全屏，而不是普通最大化。
    ///
    /// 普通最大化只铺满「工作区」，任务栏还在；全屏要铺满整个显示器。
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            // 全屏期间只关心最小化/还原。
            // 最小化时必须收起浮层：Popup 是独立窗口且 StaysOpen=True，
            // 不管它的话会一直飘在桌面上，看着像程序没关干净。
            if (WindowState == WindowState.Minimized)
            {
                HideFullscreenBar();
            }
            else if (WindowState == WindowState.Normal && !FullscreenBarPopup.IsOpen)
            {
                ShowFullscreenBar();
            }

            return;
        }

        if (_suppressStateChange || WindowState != WindowState.Maximized)
        {
            return;
        }

        // 先退回普通状态，再按全屏方式重新摆放；否则会和系统最大化的布局打架。
        _suppressStateChange = true;
        try
        {
            WindowState = WindowState.Normal;
        }
        finally
        {
            _suppressStateChange = false;
        }

        EnterFullscreen();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isFullscreen && !_barPinned)
        {
            HideFullscreenBar();
        }
    }

    /// <summary>
    /// 进入全屏。
    /// </summary>
    internal void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        _stateBeforeFullscreen = WindowState;
        _boundsBeforeFullscreen = new Rect(Left, Top, ActualWidth, ActualHeight);
        _styleBeforeFullscreen = WindowStyle;
        _resizeBeforeFullscreen = ResizeMode;
        _smartSizingBeforeFullscreen = _settings.Current.Session.SmartSizing;

        _isFullscreen = true;

        _suppressStateChange = true;
        try
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            // 任务栏是 topmost 的，普通窗口压不住它，全屏期间必须临时置顶。
            // 用 SetCurrentValue 而不是直接赋值：直接赋值会清掉 XAML 的 OneWay 绑定。
            SetCurrentValue(TopmostProperty, true);
        }
        finally
        {
            _suppressStateChange = false;
        }

        // 标题栏让位给画面。
        WindowTitleBar.Visibility = Visibility.Collapsed;
        TitleRow.Height = new GridLength(0);

        // 去掉 RDP 宿主的 3px 边距。
        //
        // 那 3px 是给窗口边缘拖拽留的命中区（样式里只在 Maximized 时才归零），
        // 而全屏时窗口状态是 Normal，样式给的仍是 3,0,3,3——
        // 于是画面比屏幕窄 6px、矮 3px。此时就算分辨率完全一致，
        // 自适应缩放也会做一次非整数重采样（例如 2560 → 2554），画面必然发虚。
        // 全屏下没有边框可拖，这个边距只会帮倒忙。
        RdpFormsHost.ClearValue(FrameworkElement.MarginProperty);
        RdpFormsHost.Margin = new Thickness(0);

        ApplyFullscreenBounds();

        UpdatePinButtons();
        ShowFullscreenBar();
        StartCursorWatch();
        StartRemoteSizeWatch();
        NotifyResolutionMismatch();
    }

    /// <summary>
    /// 退出全屏，恢复原来的窗口状态。
    /// </summary>
    internal void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _isFullscreen = false;
        StopCursorWatch();
        StopRemoteSizeWatch();
        HideFullscreenBar();

        _suppressStateChange = true;
        try
        {
            WindowStyle = _styleBeforeFullscreen;
            ResizeMode = _resizeBeforeFullscreen;
            WindowTitleBar.Visibility = Visibility.Visible;
            TitleRow.Height = GridLength.Auto;

            // 还原样式里给 RDP 宿主留的 3px 命中区（全屏时被清成 0 了）。
            RdpFormsHost.ClearValue(FrameworkElement.MarginProperty);

            SetCurrentValue(TopmostProperty, _settings.Current.Session.Topmost);

            WindowState = WindowState.Normal;
            Left = _boundsBeforeFullscreen.Left;
            Top = _boundsBeforeFullscreen.Top;
            Width = _boundsBeforeFullscreen.Width;
            Height = _boundsBeforeFullscreen.Height;

            if (_stateBeforeFullscreen == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        finally
        {
            _suppressStateChange = false;
        }

        _childSessionService.SetSmartSizing(_smartSizingBeforeFullscreen, persist: false);
    }

    /// <summary>
    /// 把窗口贴合到当前显示器，并按最新屏幕尺寸决定缩放模式。
    ///
    /// 进入全屏、以及显示器分辨率变化时都会调用，所以不能依赖进入时的缓存值。
    /// </summary>
    private void ApplyFullscreenBounds()
    {
        if (!WindowNativeMethods.TryGetMonitorRect(new WindowInteropHelper(this).Handle, out var monitor))
        {
            return;
        }

        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        var dpi = VisualTreeHelper.GetDpi(this);

        Left = monitor.Left / dpi.DpiScaleX;
        Top = monitor.Top / dpi.DpiScaleY;
        Width = monitorWidth / dpi.DpiScaleX;
        Height = monitorHeight / dpi.DpiScaleY;

        UpdateFullscreenScaling(monitorWidth, monitorHeight);
    }

    /// <summary>
    /// 按「分身分辨率是否等于屏幕分辨率」决定全屏下的缩放模式。
    ///
    /// 一致 → 1:1：像素一一对应，不重采样，这是最清晰的一档；
    /// 不一致 → 自适应：反正都要缩放，优先保证铺满。
    /// 两者都只改运行时行为，不写设置，退出全屏时还原用户原本的选择。
    /// </summary>
    private void UpdateFullscreenScaling(int monitorWidth, int monitorHeight)
    {
        var session = _settings.Current.Session;
        var matchesScreen = monitorWidth > 0
            && monitorHeight > 0
            && session.DesktopWidth == monitorWidth
            && session.DesktopHeight == monitorHeight;

        _childSessionService.SetSmartSizing(!matchesScreen, persist: false);
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmDisplayChange = 0x007E;

        if (message == WmDisplayChange && _isFullscreen)
        {
            // 等系统把新的显示信息落定再重新贴合：在消息处理里立刻读
            // monitor rect，拿到的可能还是旧的尺寸。
            _ = Dispatcher.BeginInvoke(
                new Action(OnDisplaySettingsChanged),
                DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged()
    {
        if (!_isFullscreen || !IsLoaded)
        {
            return;
        }

        ApplyFullscreenBounds();
        NotifyResolutionMismatch();
    }

    /// <summary>
    /// 盯着「分身内部」的分辨率。
    ///
    /// 用户可能在分身里自己打开「显示设置」改分辨率——那不会经过我们的菜单，
    /// 也没有任何事件通知。只能定期读一次 RDP 控件的实际尺寸，
    /// 发现变了就同步回设置并重新判定缩放模式。
    ///
    /// 只在全屏期间轮询：非全屏时窗口布局与缩放都不由我们强制，没有实时响应的必要。
    /// </summary>
    private void StartRemoteSizeWatch()
    {
        _remoteSizeWatchTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _remoteSizeWatchTimer.Tick -= OnRemoteSizeTick;
        _remoteSizeWatchTimer.Tick += OnRemoteSizeTick;
        _remoteSizeWatchTimer.Start();
    }

    private void StopRemoteSizeWatch()
    {
        _remoteSizeWatchTimer?.Stop();
    }

    private void OnRemoteSizeTick(object? sender, EventArgs e)
    {
        if (!_isFullscreen)
        {
            return;
        }

        if (_childSessionService.RemoteDesktopSize is not { } size)
        {
            return;
        }

        // 值没变就什么也不做；变了才写设置并触发重新判定
        // （SyncDesktopSizeFromRemote 会发 DesktopSizeChanged，进而重新算缩放）。
        _ = _childSessionService.SyncDesktopSizeFromRemote(size.Width, size.Height);
    }

    /// <summary>
    /// 分身分辨率被修改（菜单里的「桌面分辨率」）。
    /// 全屏下要立刻重新判断该用 1:1 还是自适应，否则画面会停在旧的缩放方式上。
    /// </summary>
    private void OnDesktopSizeChanged(object? sender, EventArgs e)
    {
        if (!_isFullscreen)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(ApplyFullscreenBounds),
            DispatcherPriority.Background);
    }

    private void OnFullscreenMinimizeClicked(object sender, RoutedEventArgs e)
    {
        // 先收浮层再最小化：Popup 不随窗口最小化一起隐藏。
        HideFullscreenBar();
        WindowState = WindowState.Minimized;
    }

    private void OnFullscreenRestoreClicked(object sender, RoutedEventArgs e)
    {
        ExitFullscreen();
    }

    private void OnFullscreenCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnFullscreenTogglePinClicked(object sender, RoutedEventArgs e)
    {
        _barPinned = !_barPinned;
        _settings.Current.Window.FullscreenBarPinned = _barPinned;
        _settings.SaveSoon();

        UpdatePinButtons();

        if (_barPinned)
        {
            _barAutoHideTimer?.Stop();
            ShowFullscreenBar(keepOpen: true);
        }
        else
        {
            // 取消固定后按普通规则处理：再显示 10 秒，然后自动收起。
            ShowFullscreenBar();
        }
    }

    private void UpdatePinButtons()
    {
        FullscreenPinOnButton.Visibility = _barPinned ? Visibility.Collapsed : Visibility.Visible;
        FullscreenPinOffButton.Visibility = _barPinned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 显示顶栏。固定状态下不会自动收起。
    /// </summary>
    private void ShowFullscreenBar(bool keepOpen = false)
    {
        if (!_isFullscreen)
        {
            return;
        }

        FullscreenBarPopup.IsOpen = true;

        // Popup 是独立窗口，位置要自己算：水平居中，距顶部 10。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FullscreenBarPopup.Child is FrameworkElement bar && bar.ActualWidth > 0)
            {
                FullscreenBarPopup.HorizontalOffset = Math.Max(0, (ActualWidth - bar.ActualWidth) / 2);
                FullscreenBarPopup.VerticalOffset = 10;
            }
        }), DispatcherPriority.Loaded);

        _barAutoHideTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _barAutoHideTimer.Stop();

        if (_barPinned || keepOpen)
        {
            return;
        }

        _barAutoHideTimer.Interval = TimeSpan.FromSeconds(FullscreenBarAutoHideSeconds);
        _barAutoHideTimer.Tick -= OnBarAutoHideTick;
        _barAutoHideTimer.Tick += OnBarAutoHideTick;
        _barAutoHideTimer.Start();
    }

    private void OnBarAutoHideTick(object? sender, EventArgs e)
    {
        _barAutoHideTimer?.Stop();

        if (!_barPinned)
        {
            HideFullscreenBar();
        }
    }

    private void HideFullscreenBar()
    {
        _barAutoHideTimer?.Stop();
        FullscreenBarPopup.IsOpen = false;
    }

    /// <summary>
    /// 监听鼠标是否移到屏幕顶端。
    ///
    /// 不能用 WPF 的 MouseMove：全屏时整个画面被 WindowsFormsHost 盖住，
    /// 鼠标消息进不到 WPF 这一层。只能轮询光标位置。
    /// </summary>
    private void StartCursorWatch()
    {
        _cursorWatchTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };

        _cursorWatchTimer.Tick -= OnCursorWatchTick;
        _cursorWatchTimer.Tick += OnCursorWatchTick;
        _cursorWatchTimer.Start();
    }

    private void StopCursorWatch()
    {
        _cursorWatchTimer?.Stop();
    }

    private void OnCursorWatchTick(object? sender, EventArgs e)
    {
        if (!_isFullscreen)
        {
            return;
        }

        if (!WindowNativeMethods.TryGetCursorPosition(out var x, out var y))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!WindowNativeMethods.TryGetWindowRect(handle, out var rect))
        {
            return;
        }

        var insideHorizontally = x >= rect.Left && x <= rect.Right;
        var nearTop = y >= rect.Top && y <= rect.Top + FullscreenBarHotZone * 3;

        if (!insideHorizontally || !nearTop)
        {
            return;
        }

        if (!FullscreenBarPopup.IsOpen)
        {
            ShowFullscreenBar();
        }
        else if (!_barPinned)
        {
            // 已经在热区里，重置自动收起计时，避免鼠标还没离开就收起来。
            ShowFullscreenBar();
        }
    }

    /// <summary>
    /// 全屏时如果分身分辨率和屏幕分辨率不一致，提示可能模糊。
    /// </summary>
    private void NotifyResolutionMismatch()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!WindowNativeMethods.TryGetMonitorRect(handle, out var monitor))
        {
            return;
        }

        viewModel.NotifyFullscreenResolution(monitor.Right - monitor.Left, monitor.Bottom - monitor.Top);
    }

    private async Task ConfirmAndLogoffAsync()    {
        _closeRequestInProgress = true;
        try
        {
            if (!_childSessionService.HasActiveChildSession())
            {
                ExitApplication();
                return;
            }

            var confirmed = await Dialogs.ConfirmAsync(
                "关闭会断开 RDP 并注销桌面分身，其中所有正在运行的程序都会被关闭，未保存的数据会丢失。是否继续？",
                "关闭桌面分身");
            if (!confirmed)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.LogoffAndHideAsync();
            }
            else
            {
                await _childSessionService.LogoffAndHideAsync();
            }

            ExitApplication();
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    /// <summary>
    /// 立即退出，不弹确认框。供托盘「退出」使用。
    ///
    /// 仍然会注销分身会话——不注销的话会话会残留在系统里，
    /// 下次启动还得先处理它。只是省掉了那一问。
    /// </summary>
    internal async Task ExitImmediatelyAsync()
    {
        AllowClose = true;

        try
        {
            await _childSessionService.LogoffAndHideAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or ObjectDisposedException)
        {
            AppLog.Warn("退出时注销分身会话失败，仍继续退出。", exception);
        }

        if (Application.Current is { } application)
        {
            application.Shutdown();
            return;
        }

        Close();
    }

    /// <summary>
    /// 结束进程。
    ///
    /// 这里必须是「退出」而不是「隐藏」：单实例互斥体还握在手上，
    /// 只隐藏窗口的话用户再次双击只会看到「已经在运行了」，
    /// 而任务栏里又没有这个窗口——只能靠托盘图标或再启动一次唤回。
    /// 点 X 的语义就是退出，不该把进程留在后台。
    /// </summary>
    private void ExitApplication()
    {
        // 放行 OnClosing 里的拦截，否则 Shutdown 关闭窗口时又会被当成用户点 X，再次弹确认框。
        AllowClose = true;

        if (Application.Current is { } application)
        {
            application.Shutdown();
            return;
        }

        Close();
    }
}
