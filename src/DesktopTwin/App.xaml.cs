using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTwin.Localization;
using DesktopTwin.Services;
using DesktopTwin.View;
using DesktopTwin.ViewModel;

// 命名空间和类都叫 Localization，直接写 Localization.X 会被解析成命名空间，这里起个别名。
using AppLocale = DesktopTwin.Localization.Locale;

namespace DesktopTwin;

/// <summary>
/// 应用入口。负责组装服务与主窗口，并统一处理未捕获异常。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\DesktopTwin.SingleInstance";

    /// <summary>
    /// 第二个实例用它通知已运行的实例「把窗口叫到前台」。
    /// </summary>
    private const string ActivateEventName = @"Global\DesktopTwin.Activate";

    private SettingsService? _settings;
    private AutoLoginService? _autoLogin;
    private ChildSessionService? _childSessionService;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateRegistration;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 命令行入口要先处理：它只生成语言模板，不该走到单实例检查（否则已有实例在跑时
        // 会变成「把旧窗口叫到前台」，什么都不生成），也不该创建任何窗口。
        if (LocalizationCommandLine.TryHandle(e.Args))
        {
            Shutdown();
            return;
        }

        // 界面语言：只认程序同目录同名的 .lng，没有就用内置简体中文。
        AppLocale.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // 面包屑：正常退出（含 Environment.Exit）会走 ProcessExit 并留下这一行；
        // 进程被外部强杀则不会。排查「程序突然消失」时，看日志有没有这一行就能分清是
        // 「自己退了」还是「被别人杀了」——这一条是拿这次实测的教训换来的。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            AppLog.Info("进程退出（ProcessExit 回调触发：正常退出路径）");

        // 只允许一个实例：两个实例同时操作子会话会互相把对方的会话注销掉。
        // 创建失败（例如另一个用户已用 Global\ 建过同名互斥体）时按「已在运行」处理，
        // 不要让启动过程直接抛异常。
        var createdNew = false;
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceMutexName,
                out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            createdNew = false;
        }

        if (!createdNew)
        {
            // 已有实例在跑。正常情况下它只是被隐藏了，通知它把窗口显示出来即可——
            // 这才是「再次双击图标」应有的行为，而不是弹一句「已经在运行了」让用户自己去找。
            if (TrySignalExistingInstance())
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                Locale.Get("App.AlreadyRunning"),
                "桌面分身",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 只有真正持有互斥体时才记录，OnExit 里据此决定是否 ReleaseMutex。
        _ownsSingleInstanceMutex = true;

        AppLog.Info($"DesktopTwin 启动，版本 {typeof(App).Assembly.GetName().Version}");

        StartActivationListener();

        // 上一次异常退出可能留下临时计划任务，启动时在后台清扫，不阻塞界面。
        _ = Task.Run(ChildSessionProcessLauncher.SweepOrphanTasks);

        _settings = new SettingsService();

        // 深扫义务跨重启保持：上次没跑完一次干净的完整深扫时，这次启动接着深扫。
        ChildSessionEnvironmentCheck.RestoreDeepScanObligation(
            _settings.Current.PersistenceDeepScanPending);

        var settingsForDeepScan = _settings;
        ChildSessionEnvironmentCheck.DeepScanObligationChanged = pending =>
        {
            settingsForDeepScan.Current.PersistenceDeepScanPending = pending;

            if (pending)
            {
                // 置位要立刻落盘：这条义务的全部意义就是「程序异常退出也不丢」，
                // 而 SaveSoon 有 800 毫秒延迟，正好覆盖不了崩溃窗口。
                settingsForDeepScan.Flush();
            }
            else
            {
                settingsForDeepScan.SaveSoon();
            }
        };

        _autoLogin = new AutoLoginService(_settings);
        _childSessionService = new ChildSessionService(_settings, _autoLogin);

        // 窗口由服务按需创建（首次显示或首次连接时），这里只负责触发显示，
        // 避免出现「服务持有一个窗口、App 又建了一个窗口」的重复实例。
        _childSessionService.ShowWindow();
        _mainWindow = _childSessionService.Window;
        MainWindow = _mainWindow;

        // 托盘图标常驻：窗口被隐藏后，这是唯一能把它叫回来的入口。
        _trayIcon = new TrayIconService(_childSessionService);
    }

    /// <summary>
    /// 弹一条托盘气泡。窗口最小化/隐藏时（例如视角切换的收尾），
    /// 窗口内的通知条用户看不见，这是唯一可见的提示通道。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    public static void ShowTrayBalloon(string title, string message)
    {
        if (Current is not App { _trayIcon: { } tray })
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            tray.ShowBalloon(title, message);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() => tray.ShowBalloon(title, message)));
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("DesktopTwin 退出");

        // 托盘先释放：它会订阅服务的状态事件，服务释放后回调会打到已失效的对象上。
        _trayIcon?.Dispose();
        _trayIcon = null;

        _childSessionService?.Dispose();
        _autoLogin?.Dispose();
        _settings?.Dispose();

        // ReleaseMutex 只能由持有者线程调用，未持有（第二个实例）时调用会抛 ApplicationException。
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        // 先注销等待再释放句柄，否则回调可能在已释放的句柄上触发。
        _activateRegistration?.Unregister(waitObject: null);
        _activateRegistration = null;
        _activateEvent?.Dispose();
        _activateEvent = null;

        base.OnExit(e);
    }

    /// <summary>
    /// 创建「显示窗口」通知事件，并在后台等待它被第二个实例触发。
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivateEventName);

            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                waitObject: _activateEvent,
                callBack: static (state, _) =>
                {
                    if (state is App app)
                    {
                        app.BringToForeground();
                    }
                },
                state: this,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or IOException
                                              or NotSupportedException)
        {
            // 通知不到也不影响主流程，最坏情况是第二个实例退化成弹提示框。
            AppLog.Warn("无法创建单实例通知事件，再次启动时将只提示而不唤起窗口。", exception);
            _activateEvent?.Dispose();
            _activateEvent = null;
        }
    }

    /// <summary>
    /// 通知已运行的实例把窗口叫到前台。
    /// </summary>
    /// <returns>成功通知到返回 true。</returns>
    private static bool TrySignalExistingInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
            return true;
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法通知已运行的实例显示窗口。", exception);
            return false;
        }
    }

    /// <summary>
    /// 把主窗口从隐藏/最小化状态恢复到前台。
    /// </summary>
    private void BringToForeground()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_childSessionService is null)
            {
                return;
            }

            // 窗口可能已被隐藏（「隐藏」按钮），ShowWindow 会重新显示它。
            _childSessionService.ShowWindow();

            var window = _childSessionService.Window;
            if (window is null)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            ForceForeground(window);
        }));
    }

    /// <summary>
    /// 把窗口顶到最前面。
    ///
    /// 系统有前台锁，<see cref="Window.Activate"/> 在窗口被别的程序挡住时经常无效，
    /// 「临时置顶再还原」是常用解法。这里必须用 <c>SetCurrentValue</c>：
    /// 直接给 <c>Topmost</c> 赋值会清掉 XAML 里的 OneWay 绑定，置顶按钮从此失效（这个坑踩过一次）。
    /// </summary>
    private static void ForceForeground(Window window)
    {
        var original = window.Topmost;
        window.SetCurrentValue(Window.TopmostProperty, true);
        window.SetCurrentValue(Window.TopmostProperty, original);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面线程未处理异常。", e.Exception);

        // 界面线程异常直接终止进程体验很差，这里提示后继续运行；
        // 真正的状态问题会由后续操作再次暴露并写入日志。
        MessageBox.Show(
            Locale.Format("App.UnhandledError", e.Exception.GetBaseException().Message),
            "DesktopTwin",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("后台线程未处理异常。", exception);
        }
    }
}
