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

        // 窗口外观设置在构造后立即应用一次，避免首帧跳动。
        Topmost = settings.Current.Session.Topmost;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
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
        RdpFormsHost.Child = null;
        RdpHost.Dispose();
        base.OnClosed(e);
    }

    private async Task ConfirmAndLogoffAsync()
    {
        _closeRequestInProgress = true;
        try
        {
            if (!_childSessionService.HasActiveChildSession())
            {
                Hide();
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
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }
}
