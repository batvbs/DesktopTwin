using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    private readonly ChildSessionService _childSessionService;
    private bool _closeRequestInProgress;

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
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
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
    }

    private async Task ConfirmAndLogoffAsync()
    {
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
