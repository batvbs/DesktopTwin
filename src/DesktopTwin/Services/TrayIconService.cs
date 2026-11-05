using System;
using System.Drawing;
using System.IO;
using System.Windows;
using DesktopTwin.View;
using Forms = System.Windows.Forms;

namespace DesktopTwin.Services;

/// <summary>
/// 托盘图标。
///
/// 存在的理由：窗口被「隐藏」之后，本程序没有任务栏按钮、也没有别的入口，
/// 用户只能靠再次启动程序来唤回。托盘图标给这条路径一个看得见的落点，
/// 顺便把「显示 / 隐藏 / 退出」三个动作放到右键菜单里。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const string IconResourceUri = "pack://application:,,,/Assets/DesktopTwin.ico";

    private readonly ChildSessionService _childSessionService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _hideItem;
    private bool _disposed;

    /// <summary>
    /// 创建托盘图标并立即显示。
    /// </summary>
    /// <param name="childSessionService">用于显示/隐藏窗口。</param>
    public TrayIconService(ChildSessionService childSessionService)
    {
        _childSessionService = childSessionService;

        _showItem = new Forms.ToolStripMenuItem("显示主窗口", image: null, OnShowClicked);
        _hideItem = new Forms.ToolStripMenuItem("隐藏窗口", image: null, OnHideClicked);
        var exitItem = new Forms.ToolStripMenuItem("退出", image: null, OnExitClicked);

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "桌面分身",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseClick += OnIconMouseClick;
        _notifyIcon.DoubleClick += OnIconDoubleClick;

        _childSessionService.StateChanged += OnStateChanged;
        UpdateMenuState();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _childSessionService.StateChanged -= OnStateChanged;

        // 先隐藏再释放：否则图标会一直留到用户把鼠标划过通知区域才消失。
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= OnIconMouseClick;
        _notifyIcon.DoubleClick -= OnIconDoubleClick;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private void OnIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ShowWindow();
        }
    }

    private void OnIconDoubleClick(object? sender, EventArgs e)
    {
        ShowWindow();
    }

    private void OnShowClicked(object? sender, EventArgs e)
    {
        ShowWindow();
    }

    private void OnHideClicked(object? sender, EventArgs e)
    {
        try
        {
            _childSessionService.HideWindow();
        }
        catch (Exception exception) when (exception is ObjectDisposedException
                                              or InvalidOperationException)
        {
            AppLog.Warn("从托盘隐藏窗口失败。", exception);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        // 走窗口的关闭流程：那里有「会注销分身会话」的二次确认，
        // 托盘直接退出会绕过确认，把用户正在跑的程序一起关掉。
        var window = _childSessionService.Window;
        if (window is null)
        {
            Application.Current?.Shutdown();
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Close();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 状态事件理论上都在界面线程上发出，但服务里也有 Task.Run 之后回调的路径。
        // 托盘菜单属于 WinForms 对象，统一回到界面线程再改，省得排查跨线程问题。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(UpdateMenuState));
            return;
        }

        UpdateMenuState();
    }

    private void UpdateMenuState()
    {
        if (_disposed)
        {
            return;
        }

        var visible = _childSessionService.IsDesktopVisible;
        _showItem.Enabled = !visible;
        _hideItem.Enabled = visible;
        _notifyIcon.Text = visible ? "桌面分身（窗口已显示）" : "桌面分身（窗口已隐藏）";
    }

    private void ShowWindow()
    {
        try
        {
            _childSessionService.ShowWindow();
            var window = _childSessionService.Window;
            if (window is not null)
            {
                window.Activate();
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException
                                              or InvalidOperationException)
        {
            AppLog.Warn("从托盘显示窗口失败。", exception);
        }
    }

    /// <summary>
    /// 取托盘图标。
    ///
    /// 显式指定 32×32：ico 里的 256 档是 PNG 压缩的，<see cref="Icon"/> 的解析器对
    /// PNG 编码的目录项支持有限，按尺寸取可以稳定命中 BMP 编码的那几档。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri(IconResourceUri, UriKind.Absolute));
            if (resource?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream, new System.Drawing.Size(32, 32));
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or ArgumentException
                                              or UriFormatException)
        {
            AppLog.Warn("读取内置图标失败，改用可执行文件自带的图标。", exception);
        }

        // 兜底：exe 的 Win32 图标资源（csproj 的 ApplicationIcon 写入）由 shell 直接解出。
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executablePath)
            && Icon.ExtractAssociatedIcon(executablePath) is { } extracted)
        {
            return extracted;
        }

        return SystemIcons.Application;
    }
}
