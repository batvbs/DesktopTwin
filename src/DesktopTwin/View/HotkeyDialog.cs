using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopTwin.Services;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;
using DesktopTwin.Localization;

namespace DesktopTwin.View;

/// <summary>
/// 全局热键录制框：直接按下组合键即可。
///
/// 和「输入一串文本」相比，录制不会录进拼写错误，也能天然拦下「没有修饰键」这种
/// 会把整个系统按键吃掉的危险配置。
///
/// 非模态：录制期间分身窗口仍可操作（见 <see cref="Dialogs"/>）。
/// </summary>
internal static class HotkeyDialog
{
    /// <summary>
    /// 弹出录制框。
    /// </summary>
    /// <param name="current">当前组合键，形如 <c>Ctrl+Alt+U</c>。</param>
    /// <returns>录制成功返回新组合键，取消返回 null。</returns>
    internal static Task<string?> ShowAsync(string current)
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var dialog = Dialogs.CreateWindow(Locale.Get("Hotkey.Title"), 420);

        var preview = new TextBlock
        {
            Text = GlobalHotkeyService.Format(current),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var hint = new TextBlock
        {
            Text = Locale.Get("Hotkey.Hint"),
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("Hotkey.Intro"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(preview);
        panel.Children.Add(hint);
        dialog.Content = panel;

        // 用 PreviewKeyDown 而不是 KeyDown：Tab、方向键这类键会被控件先吃掉。
        dialog.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;

            if (key == Key.Escape)
            {
                completion.TrySetResult(null);
                dialog.Close();
                return;
            }

            var modifiers = Keyboard.Modifiers;

            if (IsModifierKey(key))
            {
                // 只按了修饰键：先给个预览，等主键按下来再定稿。
                var partial = DescribeModifiers(modifiers);
                preview.Text = string.IsNullOrEmpty(partial) ? "…" : partial + " + …";
                preview.Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
                return;
            }

            if (modifiers == ModifierKeys.None)
            {
                preview.Text = Locale.Get("Hotkey.NeedModifier");
                preview.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3D));
                return;
            }

            if (GlobalHotkeyService.KeyToToken(key) is not { } token)
            {
                preview.Text = Locale.Get("Hotkey.Unsupported");
                preview.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3D));
                return;
            }

            var combination = DescribeModifiers(modifiers) + "+" + token;
            preview.Text = GlobalHotkeyService.Format(combination);
            preview.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xB9, 0xF4));
            completion.TrySetResult(combination);
            dialog.Close();
        };

        dialog.Closed += (_, _) => completion.TrySetResult(null);

        Dialogs.Show(dialog);
        return completion.Task;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    private static string DescribeModifiers(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return string.Join('+', parts);
    }
}
