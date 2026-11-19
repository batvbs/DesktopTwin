using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Services;
using DesktopTwin.ViewModel;
using Wpf.Ui.Controls;
using DesktopTwin.Localization;

namespace DesktopTwin.View;

/// <summary>
/// 桌面分身主窗口：标题栏放控制入口，内容区承载 RDP 画面。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private const double FullscreenBarAutoHideSeconds = 6;

    /// <summary>
    /// 全屏浮层工具栏距屏幕顶端的位置。
    /// </summary>
    private const double FullscreenBarTopOffset = 10;

    /// <summary>
    /// 全屏浮层工具栏自身高度：Border 上下内边距 6×2 + 按钮 30 + 边框 1×2。
    /// 改工具栏样式时要一起调，通知条的位置依赖它。
    /// </summary>
    private const double FullscreenBarHeight = 44;

    /// <summary>
    /// 鼠标进入窗口顶部这个高度（DIP）内就唤出工具栏。
    /// </summary>
    private const double FullscreenBarHotZone = 6;

    /// <summary>
    /// 通知条在正常模式下距窗口顶端的位置（让开 32px 的标题栏）。
    /// </summary>
    private const double NormalNotificationOffset = 32;

    /// <summary>
    /// 通知条在全屏模式下距屏幕顶端的位置。
    ///
    /// 必须让开全屏浮层工具栏（VerticalOffset 10 + 自身高约 44），否则两者重叠：
    /// 切观看/控制模式的提示正好会在工具栏唤出时弹出来，会被工具栏压住。
    /// </summary>
    private const double FullscreenNotificationOffset = FullscreenBarTopOffset + FullscreenBarHeight + 10;

    private readonly ChildSessionService _childSessionService;
    private readonly SettingsService _settings;
    private bool _closeRequestInProgress;

    private bool _isFullscreen;
    private bool _suppressStateChange;
    private bool _barPinned;
    private bool _wasMinimized;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen = WindowStyle.SingleBorderWindow;
    private ResizeMode _resizeBeforeFullscreen = ResizeMode.CanResize;
    private WindowCornerPreference _cornerPreferenceBeforeFullscreen;
    private WindowBackdropType _backdropTypeBeforeFullscreen;
    private Brush _backgroundBeforeFullscreen = Brushes.Black;
    private double _minWidthBeforeFullscreen;
    private double _maxWidthBeforeFullscreen;
    private double _minHeightBeforeFullscreen;
    private double _maxHeightBeforeFullscreen;
    private bool _smartSizingBeforeFullscreen;
    private DispatcherTimer? _barAutoHideTimer;
    private DispatcherTimer? _cursorWatchTimer;

    /// <summary>
    /// 凭据框轮询间隔。取 400ms：比 AutoLoginService 的 600ms 略快，
    /// 但足够避免无谓的 CPU 占用；凭据框出现到被拉到前台最多差这一拍。
    /// </summary>
    private const int CredentialDialogPollMilliseconds = 400;

    private readonly object _credentialDialogWatchGate = new();
    private System.Threading.Timer? _credentialDialogWatchTimer;
    private IntPtr _windowHandle;
    private IntPtr _revealedCredentialDialog;
    private bool _topmostSuppressedForCredential;
    private bool _credentialDialogWatchEnabled;

    /// <summary>
    /// 窗口当前是否前台。UI 线程在 Activate/Deactivate 里更新，轮询线程读取——
    /// 读写都走 <see cref="_credentialDialogWatchGate"/>，保证线程间可见。
    /// </summary>
    private bool _windowIsActive;

    /// <summary>
    /// 有一条通知因为窗口隐藏没能重新打开，等可见时补上。
    /// 排队回调里窗口若正好被隐藏，这个状态不能丢，否则通知就没了。
    /// </summary>
    private bool _notificationReopenPending;
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

        // 焦点回到窗口时恢复全屏置顶（见 OnWindowActivated 的说明）。
        Activated += OnWindowActivated;

        // 窗口从「隐藏」恢复显示（托盘唤回）时，Popup 的定位同样会失效。
        IsVisibleChanged += OnWindowVisibilityChanged;

        // 分身分辨率被改时，全屏下的「1:1 / 自适应」判定前提也变了。
        _childSessionService.DesktopSizeChanged += OnDesktopSizeChanged;

        // 通知条的 IsOpen 是两向绑定的：窗口隐藏期间任何一次 ShowNotification
        // （例如 UU 串流已恢复）都会把它重新打开，而它是独立 HWND，会孤零零浮在桌面上。
        NotificationPopup.Opened += OnNotificationPopupOpened;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose || Dispatcher.HasShutdownStarted)
        {
            // 确认要关了才收浮层：它是独立窗口，不跟着主窗口一起消失。
            // 放在最前面的话，用户点「取消」后窗口还在全屏，工具栏却已经收起来了。
            HideFullscreenBar();
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

        StopCredentialDialogWatch();
        _credentialDialogWatchTimer?.Dispose();
        _credentialDialogWatchTimer = null;

        _childSessionService.DesktopSizeChanged -= OnDesktopSizeChanged;

        RdpFormsHost.Child = null;
        RdpHost.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// 「诊断与修复」菜单项被点击。
    ///
    /// 按住 Shift 时打开「全部检查项」视图（含当前没触发的条目），
    /// 用于核对文案。这是隐藏入口，所以菜单项的提示语里写了——不写没人会发现。
    /// </summary>
    private void OnOpenDiagnosticsMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var showAll = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        viewModel.ShowDiagnostics(showAll);
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
            _windowHandle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(_windowHandle);
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
        if (WindowState == WindowState.Minimized)
        {
            _wasMinimized = true;
        }
        else if (WindowState == WindowState.Normal && _wasMinimized)
        {
            _wasMinimized = false;
            ReopenPopupsAfterRestore();
        }

        if (_isFullscreen)
        {
            // 全屏期间只关心最小化/还原。
            // 最小化时必须收起浮层：Popup 是独立窗口且 StaysOpen=True，
            // 不管它的话会一直飘在桌面上，看着像程序没关干净。
            if (WindowState == WindowState.Minimized)
            {
                AppLog.Info("全屏窗口被最小化（视角切换挂后台的收尾）。");
                HideFullscreenBar();
            }
            else if (WindowState == WindowState.Normal)
            {
                AppLog.Info("全屏窗口从最小化还原。");
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
        // 轮询线程恢复置顶前会看这个标记：窗口失焦后绝不能再被顶上去盖住别的程序。
        lock (_credentialDialogWatchGate)
        {
            _windowIsActive = false;
        }

        if (!_isFullscreen)
        {
            return;
        }

        // 全屏期间窗口失焦（切到别的程序、弹出凭据框等）时临时放下置顶：
        // 置顶窗口会盖住「Windows 安全中心」凭据框这类非置顶窗口，
        // 即使它抢到了前台焦点也露不出来。放下置顶后，非置顶的前台窗口就能显示。
        // 用 SetCurrentValue 而不是直接赋值，避免清掉 XAML 里的 OneWay 绑定。
        SetCurrentValue(TopmostProperty, false);

        if (!_barPinned)
        {
            HideFullscreenBar();
        }
    }

    /// <summary>
    /// 焦点回到全屏窗口时恢复置顶，重新盖住任务栏。
    /// 只在全屏期间生效：窗口模式下的置顶完全由 ViewModel 的 IsTopmost 绑定驱动。
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        lock (_credentialDialogWatchGate)
        {
            _windowIsActive = true;
        }

        if (!_isFullscreen)
        {
            return;
        }

        SetCurrentValue(TopmostProperty, true);

        // 凭据框场景里，置顶是用 Win32 直接放下的（当时 UI 线程被模态弹窗卡住，
        // WPF 的 Topmost 属性没变过，SetCurrentValue(true) 是 no-op）。
        // 焦点回到全屏窗时再显式把 WS_EX_TOPMOST 加回去，确保一定盖住任务栏。
        RestoreTopmostAfterCredentialDialog();
    }

    /// <summary>
    /// 窗口隐藏时又被通知打开：先收掉，等窗口回来再按有效期补开。
    /// </summary>
    private void OnNotificationPopupOpened(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            return;
        }

        _notificationReopenPending = true;
        NotificationPopup.IsOpen = false;
    }

    private void OnWindowVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // 隐藏时把轮询停了，回来要接上：全屏下工具栏只能靠光标轮询唤出。
            if (_isFullscreen)
            {
                StartCursorWatch();
            }

            ReopenPopupsAfterRestore();
            return;
        }

        // 窗口被隐藏（切视角时挂后台）：Popup 是独立 HWND，不会跟着宿主一起隐藏。
        // 一个 pin 住的工具栏会孤零零留在屏幕上，而光标轮询还会按隐藏窗口的原坐标
        // 把它重新唤出来。这里一并收掉，等窗口回来时由 ReopenPopupsAfterRestore 重开。
        FullscreenBarPopup.IsOpen = false;
        StopCursorWatch();

        // 通知条也要收掉，否则它作为独立 HWND 会留在屏幕上。但「有通知」这件事要记住：
        // 它的 IsOpen 是两向绑定，一关就把状态清掉了，窗口回来时只能靠这个标记补开
        // （到期与否由视图模型的定时器判断）。
        if (NotificationPopup.IsOpen)
        {
            _notificationReopenPending = true;
        }

        NotificationPopup.IsOpen = false;
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
        _cornerPreferenceBeforeFullscreen = WindowCornerPreference;
        _backdropTypeBeforeFullscreen = WindowBackdropType;
        _backgroundBeforeFullscreen = Background;
        _minWidthBeforeFullscreen = MinWidth;
        _maxWidthBeforeFullscreen = MaxWidth;
        _minHeightBeforeFullscreen = MinHeight;
        _maxHeightBeforeFullscreen = MaxHeight;
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

        // 全屏必须是一整块不透明黑。FluentWindow 默认挂着会在全屏露馅的东西：
        // ① DWM 圆角（WindowCornerPreference=Round）→ 四个角是圆的；
        // ② 背景材质会让窗口背景变透明 → 边缘 1px 能看到背后的窗口；
        // ③ 窗口背景在浅色主题下是白的 → 缩放取整留 1px 缝时会露白边。
        // 三者都压掉，退出时再逐项还原。
        WindowCornerPreference = WindowCornerPreference.DoNotRound;
        WindowBackdropType = WindowBackdropType.None;
        Background = Brushes.Black;
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: HwndTarget target })
        {
            target.BackgroundColor = Colors.Black;
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

        // 通知条贴在画面顶端（正常模式下在标题栏下方）。
        NotificationPopup.VerticalOffset = FullscreenNotificationOffset;

        ApplyFullscreenBounds();

        UpdatePinButtons();
        ShowFullscreenBar();
        StartCursorWatch();
        StartCredentialDialogWatch();
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
        StopCredentialDialogWatch();
        HideFullscreenBar();

        NotificationPopup.VerticalOffset = NormalNotificationOffset;

        _suppressStateChange = true;
        try
        {
            WindowStyle = _styleBeforeFullscreen;
            ResizeMode = _resizeBeforeFullscreen;

            // 还原 DWM 圆角与背景材质，并恢复窗口自己的背景。
            WindowCornerPreference = _cornerPreferenceBeforeFullscreen;
            WindowBackdropType = _backdropTypeBeforeFullscreen;
            Background = _backgroundBeforeFullscreen;

            WindowTitleBar.Visibility = Visibility.Visible;
            TitleRow.Height = GridLength.Auto;

            // 还原样式里给 RDP 宿主留的 3px 命中区（全屏时被清成 0 了）。
            RdpFormsHost.ClearValue(FrameworkElement.MarginProperty);

            SetCurrentValue(TopmostProperty, _settings.Current.Session.Topmost);

            // 先还掉尺寸锁，再恢复边界：否则下面的 Width/Height 会被全屏锁掉的 min/max 夹住。
            MinWidth = _minWidthBeforeFullscreen;
            MaxWidth = _maxWidthBeforeFullscreen;
            MinHeight = _minHeightBeforeFullscreen;
            MaxHeight = _maxHeightBeforeFullscreen;

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
        // 这里也会被排队的回调调用（OnDesktopSizeChanged 用 Background 优先级排队），
        // 排队期间用户可能已经退出全屏：那时再摆尺寸、锁 min==max 会把窗口模式也锁死。
        if (!_isFullscreen)
        {
            return;
        }

        if (!WindowNativeMethods.TryGetMonitorRect(new WindowInteropHelper(this).Handle, out var monitor))
        {
            return;
        }

        var monitorWidth = monitor.Right - monitor.Left;
        var monitorHeight = monitor.Bottom - monitor.Top;
        var dpi = VisualTreeHelper.GetDpi(this);

        // 先定缩放模式，再改窗口尺寸：控件是在尺寸变化时按 SmartSizing 重排的，
        // 反过来做的话，这次 resize 用的还是上一档设置，画面要等下一次尺寸变化才跟上。
        UpdateFullscreenScaling(monitorWidth, monitorHeight);

        Left = monitor.Left / dpi.DpiScaleX;
        Top = monitor.Top / dpi.DpiScaleY;

        // 显示器分辨率/DPI 变化时这里会被再次调用（WM_DISPLAYCHANGE）。进全屏时锁死的
        // min==max 会先于 Width/Height 生效，把新尺寸夹在旧值上，窗口就贴不上新显示器。
        // 所以每次都要先释放再改尺寸，改完再按新尺寸锁死。
        MinWidth = _minWidthBeforeFullscreen;
        MaxWidth = _maxWidthBeforeFullscreen;
        MinHeight = _minHeightBeforeFullscreen;
        MaxHeight = _maxHeightBeforeFullscreen;

        Width = monitorWidth / dpi.DpiScaleX;
        Height = monitorHeight / dpi.DpiScaleY;

        // 尺寸锁死是兜底：WindowBackdropType=None 会让 WindowChrome 按 NoResize 重算
        // （缩放命中区归零），这里 min==max 再确保任何路径都缩不动，
        // 避免把「全屏」拖成别的尺寸却还停在 IsFullscreen 状态。
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
    }

    /// <summary>
    /// 全屏时决定缩放模式与黑边，三条规则按顺序判定：
    ///
    /// 1. 屏幕正好等于分身分辨率 → 1:1 铺满，两项设置都不看；
    /// 2. 屏幕长宽都 ≥ 分身分辨率，且用户关掉了「自适应缩放」→ 1:1 居中，四周黑边；
    /// 3. 其余（屏幕比分身小，或用户开着自适应）→ 自适应缩放，是否留黑边看「保持宽高比」。
    ///
    /// 都只改运行时行为，不写设置，退出全屏时还原用户原本的选择。
    /// 这里用传进来的显示器尺寸算，不读窗口的 Width/Height——调用方是先定缩放再改窗口尺寸的。
    /// </summary>
    private void UpdateFullscreenScaling(int monitorWidth, int monitorHeight)
    {
        if (monitorWidth <= 0 || monitorHeight <= 0)
        {
            return;
        }

        var session = _settings.Current.Session;
        var desktopWidth = session.DesktopWidth;
        var desktopHeight = session.DesktopHeight;

        // 规则 1：分辨率一致，1:1 铺满。
        if (desktopWidth == monitorWidth && desktopHeight == monitorHeight)
        {
            _childSessionService.SetSmartSizing(false, persist: false);
            RdpFormsHost.Margin = new Thickness(0);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var areaWidth = monitorWidth / dpi.DpiScaleX;
        var areaHeight = monitorHeight / dpi.DpiScaleY;

        // 规则 2：屏幕足够大且用户选了 1:1，按原始像素居中、四周留黑边。
        var displayCoversDesktop = monitorWidth >= desktopWidth && monitorHeight >= desktopHeight;
        if (displayCoversDesktop && !session.SmartSizing)
        {
            _childSessionService.SetSmartSizing(false, persist: false);

            var contentWidth = desktopWidth / dpi.DpiScaleX;
            var contentHeight = desktopHeight / dpi.DpiScaleY;
            var insetX = Math.Max(0d, (areaWidth - contentWidth) / 2);
            var insetY = Math.Max(0d, (areaHeight - contentHeight) / 2);
            RdpFormsHost.Margin = new Thickness(insetX, insetY, insetX, insetY);
            return;
        }

        // 规则 3：自适应缩放，黑边看「保持宽高比」。
        // 目标比例取分身桌面的实际比例，不能写死 16:9——DesktopTwin 的桌面分辨率是可配的，
        // 用户设成 1920x1200（16:10）时按 16:9 留边会把画面裁坏。
        _childSessionService.SetSmartSizing(true, persist: false);
        ApplyFullscreenLetterbox(areaWidth, areaHeight, (double)desktopWidth / desktopHeight);

        // 只有真的退到自适应缩放时才提示模糊——这是唯一会让人察觉画面变差的场景。
        // 提示走 Popup（独立窗口），所以画在外层桌面、浮在分身画面之上，不会被 RDP 画面盖住。
        NotifyResolutionMismatch();
    }

    /// <summary>
    /// 全屏下按「保持宽高比」给 RDP 画面留黑边：画面是 16:9，显示器不是时把控件缩到
    /// 居中的 16:9 矩形里，四周露出窗口的黑色背景。缩的是控件本身，RDP 的鼠标坐标映射
    /// 仍是「远端 16:9 → 控件 16:9」的等比关系，不会错位。
    /// </summary>
    private void ApplyFullscreenLetterbox(double areaWidth, double areaHeight, double targetRatio)
    {
        if (!_settings.Current.Session.KeepAspectRatio
            || areaWidth <= 0
            || areaHeight <= 0
            || !double.IsFinite(targetRatio)
            || targetRatio <= 0)
        {
            RdpFormsHost.Margin = new Thickness(0);
            return;
        }

        var areaRatio = areaWidth / areaHeight;
        if (Math.Abs(areaRatio - targetRatio) < 0.001)
        {
            RdpFormsHost.Margin = new Thickness(0);
            return;
        }

        if (areaRatio > targetRatio)
        {
            // 显示器比 16:9 宽：左右留黑边。
            var contentWidth = areaHeight * targetRatio;
            var sideInset = (areaWidth - contentWidth) / 2;
            RdpFormsHost.Margin = new Thickness(sideInset, 0, sideInset, 0);
        }
        else
        {
            // 显示器比 16:9 高（16:10、4:3）：上下留黑边。
            var contentHeight = areaWidth / targetRatio;
            var verticalInset = (areaHeight - contentHeight) / 2;
            RdpFormsHost.Margin = new Thickness(0, verticalInset, 0, verticalInset);
        }
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmDisplayChange = 0x007E;
        const int WmExitSizeMove = 0x0232;

        if (message == WmDisplayChange && _isFullscreen)
        {
            // 等系统把新的显示信息落定再重新贴合：在消息处理里立刻读
            // monitor rect，拿到的可能还是旧的尺寸。
            _ = Dispatcher.BeginInvoke(
                new Action(OnDisplaySettingsChanged),
                DispatcherPriority.Background);
        }

        // 拖动/缩放结束时校正一次位置：用户把窗口推到屏幕顶部以外时，标题栏会被挡住、
        // 窗口再也抓不回来。放在 EXITSIZEMOVE 而不是 MOVE，避免和拖动过程抢位置。
        if (message == WmExitSizeMove && !_isFullscreen)
        {
            EnsureTitleBarVisible();
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把窗口顶边压回所在显示器工作区的顶部，保证标题栏始终露得出来。
    ///
    /// 只处理「上边缘跑出屏幕」这一种：左右超出时系统会留一部分可见，用户还能拖回来；
    /// 上边缘一旦越过工作区顶部，标题栏整条看不见，只能靠任务栏或 Alt+Space 找回来。
    /// </summary>
    private void EnsureTitleBarVisible()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var workAreaTop = screen.WorkingArea.Top / (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1);

        if (Top < workAreaTop)
        {
            Top = workAreaTop;
        }
    }

    private void OnDisplaySettingsChanged()
    {
        if (!_isFullscreen || !IsLoaded)
        {
            return;
        }

        // 重新贴合新尺寸；缩放模式与模糊提示都在 UpdateFullscreenScaling 里一并处理。
        ApplyFullscreenBounds();
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

    /// <summary>
    /// 全屏工具栏里的「切换窗口」：和非全屏标题栏那个按钮走同一个命令。
    ///
    /// 全屏时标题栏不可见，没有别的地方能切窗口；这里不额外加逻辑，
    /// 只是把同一个入口搬到浮层上。
    /// </summary>
    private void OnFullscreenSwitchWindowClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SwitchWindowCommand.Execute(null);
        }
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
            // 取消固定后按普通规则处理：再显示几秒，然后自动收起。
            ShowFullscreenBar();
        }
    }

    private void UpdatePinButtons()
    {
        FullscreenPinOnButton.Visibility = _barPinned ? Visibility.Collapsed : Visibility.Visible;
        FullscreenPinOffButton.Visibility = _barPinned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 通知是否还没过期。窗口隐藏期间它可能已经到时，那就别再翻出来了。
    /// </summary>
    private bool IsNotificationStillActive()
    {
        return DataContext is MainWindowViewModel viewModel && viewModel.IsNotificationActive;
    }

    /// <summary>
    /// 窗口从最小化还原后，把还开着的 Popup 关掉重开。
    ///
    /// Popup 在 WPF 里是独立 HWND，按 PlacementTarget 计算一次坐标后就不再跟随宿主。
    /// 视角切换的收尾会把窗口最小化（挂后台），此时「正在切换视角」的通知条
    /// 可能已经打开——它记下的是最小化窗口在屏幕外（约 -32000）的位置，
    /// 窗口还原后它不会自己回来，用户看到的就是「切回时没有任何提示」。
    /// 同理，全屏工具栏在最小化期间被收起/打开的坐标也会失效，
    /// 表现成「鼠标移到顶部也唤不出按钮」。关掉再在布局完成后重开即可重新定位。
    /// </summary>
    private void ReopenPopupsAfterRestore()
    {
        if (!NotificationPopup.IsOpen && !FullscreenBarPopup.IsOpen && !_isFullscreen
            && !_notificationReopenPending)
        {
            // 什么都没开着，也没有全屏工具栏要恢复：不用折腾。
            return;
        }

        var notificationWasOpen = NotificationPopup.IsOpen || _notificationReopenPending;
        var barWasOpen = FullscreenBarPopup.IsOpen;

        NotificationPopup.IsOpen = false;
        FullscreenBarPopup.IsOpen = false;

        AppLog.Info(
            $"窗口还原：通知条原状态={notificationWasOpen}，全屏工具栏原状态={barWasOpen}，重新定位。");

        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                // 排队期间窗口可能又被隐藏（切视角时很常见）：这时候重开会把 Popup
                // 留在屏幕外或盖在隐藏窗口上。记下待重开状态，等下次可见时补。
                if (!IsVisible)
                {
                    // 用 |=：期间可能又有新通知打开、又被隐藏置位了，
                    // 直接赋值会把它清掉，恢复窗口时那条就没了。
                    _notificationReopenPending |= notificationWasOpen;
                    return;
                }

                _notificationReopenPending = false;

                if (barWasOpen || _isFullscreen)
                {
                    ShowFullscreenBar();
                }

                if (notificationWasOpen && IsNotificationStillActive())
                {
                    NotificationPopup.IsOpen = true;
                }
            }),
            DispatcherPriority.Loaded);
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

        // 只在「关→开」的边沿记日志：光标停留在顶部热区时这里每 180ms 都会进来，
        // 不加边沿判断日志会被刷爆。
        if (!FullscreenBarPopup.IsOpen)
        {
            AppLog.Info($"全屏工具栏显示（固定={_barPinned}，来源：{(keepOpen ? "手动" : "自动/热区")}）。");
        }

        FullscreenBarPopup.IsOpen = true;

        // Popup 是独立窗口，位置要自己算：水平居中，距顶部 10。
        // 首次打开时 ActualWidth 可能还是 0，退回 DesiredSize；
        // 两个都拿不到就按固定宽度估——以前这里一旦取不到宽度就完全不定位，
        // 结果工具栏停在最小化时的陈旧坐标上，看着像「没显示」。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FullscreenBarPopup.Child is FrameworkElement bar)
            {
                var barWidth = bar.ActualWidth > 0 ? bar.ActualWidth : bar.DesiredSize.Width;
                if (barWidth <= 0)
                {
                    barWidth = 300;
                }

                FullscreenBarPopup.HorizontalOffset = Math.Max(0, (ActualWidth - barWidth) / 2);
                FullscreenBarPopup.VerticalOffset = FullscreenBarTopOffset;
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
        if (FullscreenBarPopup.IsOpen)
        {
            AppLog.Info("全屏工具栏收起。");
        }

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

    /// <summary>
    /// 全屏期间启动后台轮询，主动发现「Windows 安全中心」凭据框。
    ///
    /// 不能用 DispatcherTimer：凭据框是模态的，UI 线程会卡在它的消息循环里，
    /// DispatcherTimer 跟着停摆（这正是「启动过程中工具栏唤不出」的原因）。
    /// System.Threading.Timer 跑在线程池上，不依赖 UI 线程泵消息。
    /// </summary>
    private void StartCredentialDialogWatch()
    {
        _windowHandle = new WindowInteropHelper(this).Handle;

        lock (_credentialDialogWatchGate)
        {
            _revealedCredentialDialog = IntPtr.Zero;
            _topmostSuppressedForCredential = false;
            _credentialDialogWatchEnabled = true;
            _windowIsActive = IsActive;
        }

        if (_credentialDialogWatchTimer is null)
        {
            _credentialDialogWatchTimer = new System.Threading.Timer(
                OnCredentialDialogWatchTick,
                null,
                CredentialDialogPollMilliseconds,
                CredentialDialogPollMilliseconds);
        }
        else
        {
            _credentialDialogWatchTimer.Change(
                CredentialDialogPollMilliseconds,
                CredentialDialogPollMilliseconds);
        }
    }

    /// <summary>
    /// 停止凭据框轮询，并把可能还挂着「放下置顶」的状态收干净。
    /// </summary>
    private void StopCredentialDialogWatch()
    {
        _credentialDialogWatchTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        lock (_credentialDialogWatchGate)
        {
            _credentialDialogWatchEnabled = false;
        }

        RestoreTopmostAfterCredentialDialog();
    }

    /// <summary>
    /// 凭据框轮询回调（线程池线程）。只做 Win32 调用与字段读写，
    /// 不碰任何 WPF 对象——WPF 对象只认创建它的 UI 线程。
    /// </summary>
    private void OnCredentialDialogWatchTick(object? state)
    {
        try
        {
            IntPtr handle;
            lock (_credentialDialogWatchGate)
            {
                if (!_credentialDialogWatchEnabled)
                {
                    return;
                }

                handle = _windowHandle;
            }

            if (handle == IntPtr.Zero || !WindowNativeMethods.IsWindow(handle))
            {
                return;
            }

            var dialog = AutoLoginService.FindCredentialDialog();
            if (dialog != IntPtr.Zero)
            {
                lock (_credentialDialogWatchGate)
                {
                    if (!_credentialDialogWatchEnabled || dialog == _revealedCredentialDialog)
                    {
                        return;
                    }

                    _revealedCredentialDialog = dialog;
                }

                RevealCredentialDialog(handle, dialog);
                return;
            }

            // 凭据框已经消失（用户提交/取消了），尝试恢复置顶。
            bool marshalRestore;
            lock (_credentialDialogWatchGate)
            {
                // 窗口不在前台时不动、也不清标记：等 OnWindowActivated 时由
                // RestoreTopmostAfterCredentialDialog 在 UI 线程重新检查后再恢复。
                marshalRestore = _topmostSuppressedForCredential
                                 && _credentialDialogWatchEnabled
                                 && _windowIsActive;
                _revealedCredentialDialog = IntPtr.Zero;
            }

            if (marshalRestore)
            {
                // 恢复置顶必须回到 UI 线程做：在线程池里直接 SetWindowPos，用的是
                // 「查完状态就过期」的判断，UI 线程这期间可能又失焦/退全屏了。
                _ = Dispatcher.BeginInvoke(new Action(RestoreTopmostAfterCredentialDialog));
            }
        }
        catch (Exception exception)
        {
            // 轮询跑在后台线程上，任何异常都不能把进程带崩。这一轮没拉到，
            // 下一轮还会再试。
            AppLog.Warn("全屏凭据框轮询失败，跳过本轮。", exception);
        }
    }

    /// <summary>
    /// 把刚出现的凭据框拉到前台。必须先在 Win32 层放下自己的置顶，
    /// 否则凭据框作为非置顶窗口会一直被压在全屏窗下面。
    /// </summary>
    private void RevealCredentialDialog(IntPtr handle, IntPtr dialog)
    {
        // 先把「为凭据框放下置顶」的标记记下，再放下原生置顶：万一这期间退全屏/停轮询，
        // StopCredentialDialogWatch 的 RestoreTopmostAfterCredentialDialog 能看到标记、
        // 把原生置顶收回来，不会留成「标记还没记、置顶已经放下」的死态。
        lock (_credentialDialogWatchGate)
        {
            if (!_credentialDialogWatchEnabled)
            {
                _revealedCredentialDialog = IntPtr.Zero;
                return;
            }

            _topmostSuppressedForCredential = true;
        }

        if (!WindowNativeMethods.SetWindowTopmost(handle, false))
        {
            // 放下失败（窗口可能正在销毁）：回滚刚记的标记，下一轮再试。
            lock (_credentialDialogWatchGate)
            {
                _topmostSuppressedForCredential = false;
                _revealedCredentialDialog = IntPtr.Zero;
            }

            return;
        }

        // 顺序：先还原（防最小化）→ 提到本带顶部 → 抢前台焦点。
        // 我们进程此刻就是前台进程，SetForegroundWindow 通常能成功；
        // 即便被前台锁挡住，前两步已经让弹窗可见了。
        _ = WindowNativeMethods.ShowWindow(dialog, WindowNativeMethods.SwRestore);
        _ = WindowNativeMethods.BringWindowToTop(dialog);
        _ = WindowNativeMethods.SetForegroundWindow(dialog);

        AppLog.Info($"全屏：检测到凭据框（句柄 0x{dialog.ToInt64():X}），已放下置顶并把它拉到前台。");
    }

    /// <summary>
    /// 把「为凭据框放下置顶」的状态还原。只会在 UI 线程被调用：焦点回到全屏窗
    /// （<see cref="OnWindowActivated"/>）、退全屏收尾（<see cref="StopCredentialDialogWatch"/>）、
    /// 或凭据框消失后由轮询线程 <c>Dispatcher.BeginInvoke</c> 回来。
    /// 在这里统一重新检查「仍全屏 + 窗口是前台」，避免用过期判断改 Z 序。
    /// </summary>
    private void RestoreTopmostAfterCredentialDialog()
    {
        bool shouldRestore;
        IntPtr handle;
        lock (_credentialDialogWatchGate)
        {
            // 仍在全屏时，只有窗口是前台才恢复（否则会把本窗顶上去盖住别的程序）；
            // 退出全屏时（_isFullscreen 已置 false）必须恢复原生置顶——凭据框场景里
            // 降的是原生 WS_EX_TOPMOST，WPF 的 Topmost 还是 true，不恢复的话后面
            // SetCurrentValue(Topmost, settings) 是 no-op，窗口就停在非置顶状态。
            shouldRestore = _topmostSuppressedForCredential
                            && (_windowIsActive || !_isFullscreen);
            if (shouldRestore)
            {
                _topmostSuppressedForCredential = false;
                _revealedCredentialDialog = IntPtr.Zero;
            }

            handle = _windowHandle;
        }

        if (shouldRestore && handle != IntPtr.Zero)
        {
            WindowNativeMethods.SetWindowTopmost(handle, true);
        }
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
                Locale.Get("Main.ConfirmClose.Message"),
                Locale.Get("Main.ConfirmClose.Title"));
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
