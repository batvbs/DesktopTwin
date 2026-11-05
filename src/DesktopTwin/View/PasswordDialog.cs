using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;

namespace DesktopTwin.View;

/// <summary>
/// 输入 Windows 登录密码的对话框。
///
/// 用 <see cref="PasswordBox"/> 而不是普通输入框：至少不会在录屏、截图、输入法候选里露出来。
/// 密码本身不在这里落盘，交给调用方用 DPAPI 加密后写进设置。
/// </summary>
internal static class PasswordDialog
{
    /// <summary>
    /// 弹出密码输入框。
    /// </summary>
    /// <param name="userName">将要使用的账户名，用于说明文案。</param>
    /// <returns>确认时返回密码，取消返回 null。</returns>
    internal static string? Show(string userName)
    {
        var dialog = new FluentWindow
        {
            Title = "自动登录分身",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var passwordBox = new Wpf.Ui.Controls.PasswordBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            PlaceholderText = "Windows 登录密码"
        };

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = "保存",
            Appearance = ControlAppearance.Primary,
            MinWidth = 80,
            IsDefault = true
        };

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "取消",
            MinWidth = 80,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(applyButton);

        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"自动登录会使用账户「{userName}」的 Windows 登录密码，"
                + "在重连分身时自动填入凭据框。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "密码用 DPAPI 加密后保存在本机，只有当前 Windows 账户能解开；不会上传到任何地方。",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        string? result = null;

        applyButton.Click += (_, _) =>
        {
            var password = passwordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                errorText.Text = "密码不能为空。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            result = password;
            dialog.DialogResult = true;
        };

        cancelButton.Click += (_, _) => dialog.DialogResult = false;

        dialog.Loaded += (_, _) => passwordBox.Focus();

        return dialog.ShowDialog() == true ? result : null;
    }
}
