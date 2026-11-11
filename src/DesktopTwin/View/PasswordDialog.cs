using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;
using DesktopTwin.Localization;

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
    /// <param name="validator">
    /// 校验回调：返回成功才关闭窗口并交出密码；返回失败则保留窗口、显示错误文本。
    /// 为 null 时退回旧行为（点确认就拿走密码，由调用方自行校验）。
    /// </param>
    /// <returns>验证通过时返回密码，取消或校验失败后关闭时返回 null。</returns>
    internal static Task<string?> ShowAsync(
        string userName,
        string? confirmText = null,
        IReadOnlyList<string>? storedAccounts = null,
        Func<string, Task<PasswordValidationResult>>? validator = null)
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var dialog = Dialogs.CreateWindow(Locale.Get("Password.Title"), 460);

        var passwordBox = new Wpf.Ui.Controls.PasswordBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            PlaceholderText = Locale.Get("Password.Placeholder")
        };

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // 校验期间的说明（「正在验证…」）。与错误文本分开，避免一红一灰互相覆盖。
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = confirmText ?? Locale.Get("Password.Confirm.Default"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 110,
            IsDefault = true
        };

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.Cancel"),
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
            Text = Locale.Format("Password.Intro", userName),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("Password.Note"),
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
                ? Locale.Format("Password.Accounts.Single", userName)
                : Locale.Format(
                    "Password.Accounts.Many",
                    string.Join(Locale.Get("Common.ListSeparator"), storedAccounts));

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
        panel.Children.Add(statusText);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        var busy = false;

        void SetBusy(bool value, string? message)
        {
            busy = value;
            applyButton.IsEnabled = !value;
            cancelButton.IsEnabled = !value;

            // 校验期间锁住输入框：否则用户在 validator 跑完前改了密码，
            // 校验的是旧的、返回给调用方的是新的，两边对不上。
            passwordBox.IsEnabled = !value;
            statusText.Text = message ?? string.Empty;
            statusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        applyButton.Click += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            var password = passwordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                errorText.Text = Locale.Get("Password.Error.Empty");
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (validator is null)
            {
                completion.TrySetResult(password);
                dialog.Close();
                return;
            }

            errorText.Visibility = Visibility.Collapsed;
            SetBusy(true, Locale.Get("Password.Verifying"));

            PasswordValidationResult result;
            try
            {
                result = await validator(password);
            }
            catch (Exception exception)
            {
                result = PasswordValidationResult.Fail(exception.GetBaseException().Message);
            }

            if (result.IsValid)
            {
                completion.TrySetResult(password);
                dialog.Close();
                return;
            }

            // 校验没过：窗口留着，把原因写在密码框下面，用户改一下就能再试。
            SetBusy(false, null);
            errorText.Text = result.Message;
            errorText.Visibility = Visibility.Visible;
            passwordBox.Focus();
            passwordBox.SelectAll();
        };

        // 用户一改密码就把上一次的错误收起来，避免旧提示误导。
        passwordBox.PasswordChanged += (_, _) =>
        {
            if (!busy)
            {
                errorText.Visibility = Visibility.Collapsed;
            }
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

/// <summary>
/// 密码校验结果。失败时带一句给用户看的原因，直接显示在密码框下面。
/// </summary>
/// <param name="IsValid">是否通过。</param>
/// <param name="Message">失败原因；通过时为空。</param>
internal readonly record struct PasswordValidationResult(bool IsValid, string Message)
{
    /// <summary>通过。</summary>
    public static PasswordValidationResult Ok() => new(true, string.Empty);

    /// <summary>未通过，附带原因。</summary>
    public static PasswordValidationResult Fail(string message) => new(false, message);
}
