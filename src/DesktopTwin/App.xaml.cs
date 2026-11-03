using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTwin.Services;
using DesktopTwin.View;
using DesktopTwin.ViewModel;

namespace DesktopTwin;

/// <summary>
/// 应用入口。负责组装服务与主窗口，并统一处理未捕获异常。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\DesktopTwin.SingleInstance";

    private SettingsService? _settings;
    private ChildSessionService? _childSessionService;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // 只允许一个实例：两个实例同时操作子会话会互相把对方的会话注销掉。
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "DesktopTwin 已经在运行了。\n\n请切换到已打开的窗口（可能在任务栏）。",
                "桌面分身",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppLog.Info($"DesktopTwin 启动，版本 {typeof(App).Assembly.GetName().Version}");

        // 上一次异常退出可能留下临时计划任务，启动时在后台清扫，不阻塞界面。
        _ = Task.Run(ChildSessionProcessLauncher.SweepOrphanTasks);

        _settings = new SettingsService();
        _childSessionService = new ChildSessionService(_settings);

        // 窗口由服务按需创建（首次显示或首次连接时），这里只负责触发显示，
        // 避免出现「服务持有一个窗口、App 又建了一个窗口」的重复实例。
        _childSessionService.ShowWindow();
        _mainWindow = _childSessionService.Window;
        MainWindow = _mainWindow;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("DesktopTwin 退出");

        _childSessionService?.Dispose();
        _settings?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面线程未处理异常。", e.Exception);

        // 界面线程异常直接终止进程体验很差，这里提示后继续运行；
        // 真正的状态问题会由后续操作再次暴露并写入日志。
        MessageBox.Show(
            $"发生未处理的错误：{e.Exception.GetBaseException().Message}\n\n详细信息已写入日志。",
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
