using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopTwin.Localization;
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
    internal static Task<Choice> ShowAsync(
        bool appAutoLoginEnabled,
        bool hasPassword,
        string userName,
        bool systemAutoLoginEnabled,
        string systemAutoLoginUser)
    {
        var completion = new TaskCompletionSource<Choice>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var dialog = Dialogs.CreateWindow(Locale.Get("AutoLogin.Title"), 600);
        dialog.SizeToContent = SizeToContent.Height;

        var choice = Choice.None;

        // ---------- 方式一：本程序自带 ----------
        var appStatus = appAutoLoginEnabled
            ? (hasPassword
                ? Locale.Get("AutoLogin.Status.On")
                : Locale.Get("AutoLogin.Status.OnButNoPassword"))
            : (hasPassword
                ? Locale.Get("AutoLogin.Status.OffWithPassword")
                : Locale.Get("AutoLogin.Status.Off"));

        var enableButton = new Wpf.Ui.Controls.Button
        {
            Content = hasPassword
                ? Locale.Get("AutoLogin.Button.Enable")
                : Locale.Get("AutoLogin.Button.SetAndEnable"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 140
        };

        var appCard = BuildCard(
            Locale.Get("AutoLogin.Card1.Title"),
            [
                Locale.Get("AutoLogin.Card1.P1"),
                Locale.Get("AutoLogin.Card1.P2"),
                Locale.Format("AutoLogin.Card1.P3", userName),
                appStatus
            ],
            enableButton);

        // ---------- 方式二：Windows 系统自动登录 ----------
        var systemStatus = systemAutoLoginEnabled
            ? Locale.Format("AutoLogin.Card2.Status.On", systemAutoLoginUser)
            : Locale.Get("AutoLogin.Card2.Status.Off");

        var openButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("AutoLogin.Button.OpenSystem"),
            MinWidth = 140
        };

        var systemCard = BuildCard(
            Locale.Get("AutoLogin.Card2.Title"),
            [
                Locale.Get("AutoLogin.Card2.P1"),
                Locale.Get("AutoLogin.Card2.P2"),
                Locale.Get("AutoLogin.Card2.Step1"),
                Locale.Get("AutoLogin.Card2.Step2"),
                Locale.Get("AutoLogin.Card2.Step3"),
                Locale.Get("AutoLogin.Card2.Step4"),
                systemStatus
            ],
            openButton,
            warning: Locale.Get("AutoLogin.Card2.Warning"));

        var closeButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.Close"),
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
            Text = Locale.Get("AutoLogin.Intro"),
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
            completion.TrySetResult(choice);
            dialog.Close();
        };

        openButton.Click += (_, _) =>
        {
            choice = Choice.OpenSystemAutoLogin;
            completion.TrySetResult(choice);
            dialog.Close();
        };

        closeButton.Click += (_, _) =>
        {
            completion.TrySetResult(Choice.None);
            dialog.Close();
        };

        dialog.Closed += (_, _) => completion.TrySetResult(choice);

        Dialogs.Show(dialog);
        return completion.Task;
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
