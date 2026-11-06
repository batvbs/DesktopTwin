using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

using DesktopTwin.Localization;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;

namespace DesktopTwin.View;

/// <summary>
/// 统一的对话框入口。
///
/// **全部使用非模态 <c>Show()</c>，不用 <c>ShowDialog()</c>。**
/// WPF 的 <c>ShowDialog</c> 会禁用整个应用的其它窗口，而分身画面是嵌在主窗口里的
/// WindowsFormsHost——主窗口被禁用，里面的 RDP 控件也跟着失效。
/// 表现就是「弹了个设置框，分身里点什么都没反应」。
///
/// 非模态之后结果通过 <see cref="TaskCompletionSource{TResult}"/> 回传，
/// 调用方照旧 <c>await</c>，只是界面消息循环一直在跑，分身窗口始终可用。
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// 询问用户是否继续。返回 true 表示用户选择了继续。
    /// </summary>
    /// <param name="message">正文。</param>
    /// <param name="title">标题。</param>
    /// <param name="primaryText">继续按钮文字。</param>
    /// <param name="cancelText">取消按钮文字。</param>
    internal static Task<bool> ConfirmAsync(
        string message,
        string title,
        string? primaryText = null,
        string? cancelText = null)
    {
        // 默认按钮文字要等语言文件加载后才能取，所以不能写成默认参数。
        primaryText ??= Locale.Get("Common.Continue");
        cancelText ??= Locale.Get("Common.Cancel");

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = new Wpf.Ui.Controls.Button
        {
            Content = primaryText,
            Appearance = ControlAppearance.Primary,
            MinWidth = 84
        };

        var cancel = new Wpf.Ui.Controls.Button
        {
            Content = cancelText,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };

        var window = CreateWindow(title, 520);
        window.Content = BuildLayout(message, primary, cancel);

        primary.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            window.Close();
        };

        cancel.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            window.Close();
        };

        // 用户直接点右上角关闭时按「取消」处理。
        window.Closed += (_, _) => completion.TrySetResult(false);

        Show(window);
        return completion.Task;
    }

    /// <summary>
    /// 展示一条错误信息。
    /// </summary>
    internal static Task ErrorAsync(string message, string title)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var close = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.GotIt"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 84,
            IsCancel = true
        };

        var window = CreateWindow(title, 520);
        window.Content = BuildLayout(message, close);

        close.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            window.Close();
        };

        window.Closed += (_, _) => completion.TrySetResult(true);

        Show(window);
        return completion.Task;
    }

    /// <summary>
    /// 在非异步上下文中展示错误信息（例如界面命令的兜底分支）。
    /// </summary>
    internal static void ShowError(string message, string title)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => ShowError(message, title)));
            return;
        }

        _ = ErrorAsync(message, title);
    }

    /// <summary>
    /// 创建一个统一样式的对话框窗口。调用方负责填内容并 <see cref="Show"/>。
    /// </summary>
    internal static FluentWindow CreateWindow(string title, double width)
    {
        var window = new FluentWindow
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        AttachOwner(window);
        return window;
    }

    /// <summary>
    /// 给对话框挂上主窗口作为 Owner。
    ///
    /// Owner 只影响 Z 序与「随主窗口关闭」，不会像 <c>ShowDialog</c> 那样禁用输入，
    /// 所以分身窗口仍然可以点。主窗口被隐藏时不能设 Owner（WPF 会抛异常）。
    /// </summary>
    internal static void AttachOwner(Window window)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, window) && owner.IsVisible)
        {
            window.Owner = owner;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>
    /// 显示对话框。窗口已关闭时忽略。
    /// </summary>
    internal static void Show(Window window)
    {
        try
        {
            window.Show();
        }
        catch (InvalidOperationException)
        {
            // Owner 在显示前被关掉等极端情况，退化为无 Owner 显示。
            window.Owner = null;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
    }

    /// <summary>
    /// 正文 + 按钮行的标准布局。
    /// </summary>
    private static StackPanel BuildLayout(string message, params Wpf.Ui.Controls.Button[] buttons)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        foreach (var button in buttons)
        {
            buttonRow.Children.Add(button);
        }

        panel.Children.Add(buttonRow);

        return panel;
    }
}
