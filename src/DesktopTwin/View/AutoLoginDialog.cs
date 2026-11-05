using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;

namespace DesktopTwin.View;

/// <summary>
/// 自动登录方式选择框。
///
/// 两种做法的影响范围完全不同，所以摆在一起讲清楚，让用户自己选：
/// 本程序自带的那种只作用于「重连分身」这一个动作；Windows 系统那种是整机开机即进桌面。
/// 程序不替用户做这个决定，也不替用户去改系统设置。
/// </summary>
internal static class AutoLoginDialog
{
    /// <summary>
    /// 用户在对话框里选择要做的事。
    /// </summary>
    internal enum Choice
    {
        /// <summary>什么都没选，直接关掉。</summary>
        None,

        /// <summary>启用本程序自带的自动登录（会接着弹密码框）。</summary>
        EnableAppAutoLogin,

        /// <summary>打开 Windows 的 netplwiz 去配置系统级自动登录。</summary>
        OpenSystemAutoLogin
    }

    /// <summary>
    /// 弹出对话框。
    /// </summary>
    /// <param name="appAutoLoginEnabled">本程序自动登录是否已启用。</param>
    /// <param name="hasPassword">是否已保存密码。</param>
    /// <param name="userName">自动登录使用的账户名。</param>
    /// <param name="systemAutoLoginEnabled">Windows 自动登录是否已开启。</param>
    /// <param name="systemAutoLoginUser">Windows 自动登录配置的账户。</param>
    internal static Choice Show(
        bool appAutoLoginEnabled,
        bool hasPassword,
        string userName,
        bool systemAutoLoginEnabled,
        string systemAutoLoginUser)
    {
        var dialog = new FluentWindow
        {
            Title = "自动登录设置",
            Width = 600,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var choice = Choice.None;

        // ---------- 方式一：本程序自带 ----------
        var appStatus = appAutoLoginEnabled
            ? (hasPassword ? "当前状态：已启用" : "当前状态：已启用（但没有保存密码，等同于未启用）")
            : (hasPassword ? "当前状态：未启用（已保存密码）" : "当前状态：未启用");

        var enableButton = new Wpf.Ui.Controls.Button
        {
            Content = hasPassword ? "启用自动登录" : "设置密码并启用",
            Appearance = ControlAppearance.Primary,
            MinWidth = 140
        };

        var appCard = BuildCard(
            "方式一 · 本程序自动登录（推荐）",
            [
                $"只作用于「重连桌面分身」这一个动作，不影响开机、不影响其他程序。",
                $"密码用 DPAPI 加密保存在本机，只有当前 Windows 账户能解开；不会上传到任何地方。",
                $"账户：{userName}。若系统仍弹出凭据框，程序会尝试自动填写，最多两次，之后交回手动输入。",
                appStatus
            ],
            enableButton);

        // ---------- 方式二：Windows 系统自动登录 ----------
        var systemStatus = systemAutoLoginEnabled
            ? $"当前状态：已开启（账户 {systemAutoLoginUser}）"
            : "当前状态：未开启";

        var openButton = new Wpf.Ui.Controls.Button
        {
            Content = "打开系统设置",
            MinWidth = 140
        };

        var systemCard = BuildCard(
            "方式二 · Windows 系统自动登录",
            [
                "开机后直接进入桌面，不再要求输入密码；桌面分身重连时通常也就不再弹凭据框。",
                "操作步骤：",
                "　　1. 点下面的「打开系统设置」（等同于 Win+R 输入 netplwiz）；",
                "　　2. 取消勾选「要使用本计算机，用户必须输入用户名和密码」；",
                "　　3. 点「应用」，在弹出的框里保持默认用户名不变，只输入账户密码（不是 PIN）；",
                "　　4. 点确定后重启电脑生效。",
                systemStatus
            ],
            openButton,
            warning: "注意：这会让任何能接触到这台电脑的人开机即进入你的桌面。"
                + "Windows 也会把该密码以可还原的形式保存在本机（注册表或 LSA 机密），"
                + "因此它降低的是整机安全性，不只是这一个程序的安全性。");

        var closeButton = new Wpf.Ui.Controls.Button
        {
            Content = "关闭",
            MinWidth = 88,
            IsCancel = true
        };

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonBar.Children.Add(closeButton);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "选择一种自动登录方式。两者可以同时开启，也可以都不用。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(appCard);
        panel.Children.Add(systemCard);
        panel.Children.Add(buttonBar);
        dialog.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 720,
            Content = panel
        };

        enableButton.Click += (_, _) =>
        {
            choice = Choice.EnableAppAutoLogin;
            dialog.DialogResult = true;
        };

        openButton.Click += (_, _) =>
        {
            choice = Choice.OpenSystemAutoLogin;
            dialog.DialogResult = true;
        };

        closeButton.Click += (_, _) => dialog.DialogResult = false;

        _ = dialog.ShowDialog();
        return choice;
    }

    private static Border BuildCard(
        string title,
        string[] paragraphs,
        Wpf.Ui.Controls.Button action,
        string? warning = null)
    {
        var content = new StackPanel();

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var paragraph in paragraphs)
        {
            content.Children.Add(new TextBlock
            {
                Text = paragraph,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 5)
            });
        }

        if (warning is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = warning,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Margin = new Thickness(0, 12, 0, 0);
        content.Children.Add(action);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
    }
}
