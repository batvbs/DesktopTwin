using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
///
/// 非模态：打开时分身窗口仍可操作（见 <see cref="Dialogs"/>）。
/// </summary>
internal static class PasswordDialog
{
    /// <summary>
    /// 弹出密码输入框。
    /// </summary>
    /// <param name="userName">将要使用的账户名，用于说明文案。</param>
    /// <param name="confirmText">主按钮文字，默认「保存并验证」。</param>
    /// <param name="storedAccounts">已经保存过密码的账户（不区分大小写）。</param>
    /// <returns>确认时返回密码，取消返回 null。</returns>
    internal static Task<string?> ShowAsync(
        string userName,
        string confirmText = "保存并验证",
        IReadOnlyList<string>? storedAccounts = null)
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var dialog = Dialogs.CreateWindow("自动登录分身", 460);

        var passwordBox = new Wpf.Ui.Controls.PasswordBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            PlaceholderText = "Windows 登录密码（不是 PIN）"
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
            Content = confirmText,
            Appearance = ControlAppearance.Primary,
            MinWidth = 110,
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

        // 多账户：本机换账户登录分身时，每个账户各存一份密码。
        // 这里把「已经存过谁」说清楚，用户才不会以为新密码会覆盖旧账户。
        if (storedAccounts is { Count: > 0 })
        {
            var others = new List<string>(storedAccounts.Count);
            foreach (var account in storedAccounts)
            {
                if (!string.Equals(account, userName, StringComparison.OrdinalIgnoreCase))
                {
                    others.Add(account);
                }
            }

            var hint = others.Count == 0
                ? $"已保存密码的账户：{userName}（本次会更新这一个）。"
                : $"已保存密码的账户：{string.Join("、", storedAccounts)}。"
                  + "每个账户的密码分开保存，本次只修改当前账户，不影响其它账户。";

            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        applyButton.Click += (_, _) =>
        {
            var password = passwordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                errorText.Text = "密码不能为空。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            completion.TrySetResult(password);
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            completion.TrySetResult(null);
            dialog.Close();
        };

        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Loaded += (_, _) => passwordBox.Focus();

        Dialogs.Show(dialog);
        return completion.Task;
    }
}
