using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopTwin.Models;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;

namespace DesktopTwin.View;

/// <summary>
/// 自定义分辨率输入框。
///
/// 用代码构建而不是 XAML：内容只有一个输入区，单独为它建一个 XAML + 后台文件不划算，
/// 而且这样能保证它和 <see cref="Dialogs"/> 里的其他弹窗用同一套主题控件。
/// </summary>
internal static class ResolutionDialog
{
    /// <summary>
    /// 弹出自定义分辨率对话框。
    /// </summary>
    /// <param name="width">初始宽度。</param>
    /// <param name="height">初始高度。</param>
    /// <returns>用户确认且校验通过时返回新尺寸，取消或关闭返回 null。</returns>
    internal static (int Width, int Height)? Show(int width, int height)
    {
        var widthBox = CreateSizeBox(width);
        var heightBox = CreateSizeBox(height);
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var dialog = new FluentWindow
        {
            Title = "自定义分辨率",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = "应用",
            Appearance = ControlAppearance.Primary,
            MinWidth = 80,
            IsDefault = true
        };

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "取消",
            MinWidth = 80,
            IsCancel = true
        };

        var sizePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        sizePanel.Children.Add(widthBox);
        sizePanel.Children.Add(new TextBlock
        {
            Text = "×",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        });
        sizePanel.Children.Add(heightBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(applyButton);
        // 按钮之间留出间距，避免两个按钮贴在一起。
        cancelButton.Margin = new Thickness(0, 0, 8, 0);

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "输入桌面分身的宽度和高度（像素）。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(sizePanel);
        panel.Children.Add(new TextBlock
        {
            Text = $"允许范围：{SessionSettings.MinDesktopWidth}×{SessionSettings.MinDesktopHeight}"
                + $" ~ {SessionSettings.MaxDesktopWidth}×{SessionSettings.MaxDesktopHeight}",
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });
        panel.Children.Add(errorText);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        (int Width, int Height)? result = null;

        applyButton.Click += (_, _) =>
        {
            if (!TryParseSize(widthBox.Text, out var newWidth)
                || !TryParseSize(heightBox.Text, out var newHeight))
            {
                errorText.Text = "宽度和高度都必须是整数。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (!SessionSettings.IsValidDesktopSize(newWidth, newHeight))
            {
                errorText.Text =
                    $"分辨率必须在 {SessionSettings.MinDesktopWidth}×{SessionSettings.MinDesktopHeight}"
                    + $" 到 {SessionSettings.MaxDesktopWidth}×{SessionSettings.MaxDesktopHeight} 之间。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            result = (newWidth, newHeight);
            dialog.DialogResult = true;
        };

        cancelButton.Click += (_, _) => dialog.DialogResult = false;

        // 打开后直接选中宽度，用户敲数字就能改，不用先点一下输入框。
        dialog.Loaded += (_, _) =>
        {
            widthBox.Focus();
            widthBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? result : null;
    }

    private static Wpf.Ui.Controls.TextBox CreateSizeBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 100,
        MaxLength = 4,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static bool TryParseSize(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
