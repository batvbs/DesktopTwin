using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTwin.Services;
using Wpf.Ui.Controls;

// WPF-UI 也有一个 TextBlock，同时引入两个命名空间会歧义。这里明确用 WPF 原生的。
using TextBlock = System.Windows.Controls.TextBlock;
using DesktopTwin.Localization;

namespace DesktopTwin.View;

/// <summary>
/// 诊断与修复面板。
///
/// 三档：警告（会挡住启动）/ 提示（不影响启动）/ 已解决（曾经出现、现在没了）。
/// 可自动修复的问题右侧有「修复」按钮；点整行展开完整说明与手动步骤。
///
/// 非模态，且刷新靠定时轮询而不是事件——用户可能去系统设置里改了东西再回来，
/// 轮询能自动把状态追平，不需要他手动点刷新。
/// </summary>
internal sealed class DiagnosticsWindow : FluentWindow
{
    private static readonly Color WarningColor = Color.FromRgb(0xFF, 0xC8, 0x3D);
    private static readonly Color NoticeColor = Color.FromRgb(0x60, 0xB9, 0xF4);
    private static readonly Color ResolvedColor = Color.FromRgb(0x4C, 0xD0, 0x7D);

    private readonly StackPanel _warningSection;
    private readonly StackPanel _noticeSection;
    private readonly StackPanel _resolvedSection;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>出现过的所有问题标识，用于判断「曾经有问题、现在没了」。</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>已经解决（当前检测不到）的问题标识。</summary>
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);

    /// <summary>问题标题快照，解决后仍然能显示原来的标题。</summary>
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    private bool _fixInProgress;
    private bool _closed;

    /// <summary>
    /// 创建面板。
    /// </summary>
    public DiagnosticsWindow()
    {
        Title = Locale.Get("Diag.Title");
        Width = 760;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Dialogs.AttachOwner(this);

        _warningSection = CreateSection();
        _noticeSection = CreateSection();
        _resolvedSection = CreateSection();
        _statusText = new TextBlock
        {
            Text = Locale.Get("Diag.Checking"),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };

        Content = BuildLayout();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Loaded += (_, _) => Refresh();

        // Esc 关闭：这是个非模态面板，用户随手按 Esc 走人才符合直觉。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _refreshTimer.Stop();
        };
    }

    /// <summary>
    /// 让面板刷新一次。修复完成后由调用方触发。
    /// </summary>
    internal void RefreshNow() => Refresh();

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = Locale.Get("Diag.Intro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });
        header.Children.Add(new TextBlock
        {
            Text = Locale.Get("Diag.Hint"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var sections = new StackPanel();
        sections.Children.Add(BuildSectionHeader(Locale.Get("Diag.Section.Warning"), WarningColor));
        sections.Children.Add(_warningSection);
        sections.Children.Add(BuildSectionHeader(Locale.Get("Diag.Section.Notice"), NoticeColor));
        sections.Children.Add(_noticeSection);
        sections.Children.Add(BuildSectionHeader(Locale.Get("Diag.Section.Resolved"), ResolvedColor));
        sections.Children.Add(_resolvedSection);
        scroll.Content = sections;

        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private UIElement BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_statusText, 0);
        footer.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var closeButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Common.Close"),
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        closeButton.Click += (_, _) => Close();

        var refreshButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Diag.Button.Recheck"),
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0)
        };
        refreshButton.Click += (_, _) => Refresh();

        var restartButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Diag.Button.Reboot"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 92
        };
        restartButton.Click += async (_, _) => await RestartComputerAsync();

        buttons.Children.Add(closeButton);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(restartButton);

        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        return footer;
    }

    private static TextBlock BuildSectionHeader(string title, Color color) => new()
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(color),
        Margin = new Thickness(0, 12, 0, 6)
    };

    private static StackPanel CreateSection() => new();

    /// <summary>
    /// 重新检测并重建列表。
    /// </summary>
    private void Refresh()
    {
        if (_closed)
        {
            return;
        }

        IReadOnlyList<ChildSessionEnvironmentCheck.Issue> issues;
        try
        {
            issues = ChildSessionEnvironmentCheck.Collect();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            _statusText.Text = Locale.Format("Diag.Status.CheckFailed", exception.GetBaseException().Message);
            return;
        }

        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            currentIds.Add(issue.Id);
            _titles[issue.Id] = issue.Title;
            _seen.Add(issue.Id);

            // 又出现了，说明之前的「已解决」判断作废。
            _resolved.Remove(issue.Id);
        }

        foreach (var id in _seen)
        {
            if (!currentIds.Contains(id))
            {
                _resolved.Add(id);
            }
        }

        _warningSection.Children.Clear();
        _noticeSection.Children.Clear();
        _resolvedSection.Children.Clear();

        var warningCount = 0;
        var noticeCount = 0;

        foreach (var issue in issues)
        {
            if (issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Warning)
            {
                _warningSection.Children.Add(BuildRow(issue, isResolved: false));
                warningCount++;
            }
            else
            {
                _noticeSection.Children.Add(BuildRow(issue, isResolved: false));
                noticeCount++;
            }
        }

        if (warningCount == 0)
        {
            _warningSection.Children.Add(BuildPlaceholder(Locale.Get("Diag.Empty.Warning")));
        }

        if (noticeCount == 0)
        {
            _noticeSection.Children.Add(BuildPlaceholder(Locale.Get("Diag.Empty.Notice")));
        }

        foreach (var id in _resolved)
        {
            _resolvedSection.Children.Add(BuildResolvedRow(_titles.TryGetValue(id, out var title) ? title : id));
        }

        if (_resolved.Count == 0)
        {
            _resolvedSection.Children.Add(BuildPlaceholder(Locale.Get("Diag.Empty.Resolved")));
        }

        if (!_fixInProgress)
        {
            _statusText.Text = warningCount == 0
                ? Locale.Get("Diag.Status.Clean")
                : Locale.Format("Diag.Status.Summary", warningCount, noticeCount);
        }
    }

    private TextBlock BuildPlaceholder(string text) => new()
    {
        Text = text,
        Opacity = 0.5,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 2, 2, 6)
    };

    /// <summary>
    /// 一行问题：标题 + 简述，点开显示完整说明，右侧是修复按钮。
    /// </summary>
    private Border BuildRow(ChildSessionEnvironmentCheck.Issue issue, bool isResolved)
    {
        var accent = issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Warning
            ? WarningColor
            : NoticeColor;

        var detail = new TextBlock
        {
            Text = issue.Detail,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock
        {
            Text = issue.Title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = issue.Summary,
            Opacity = 0.65,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        textPanel.Children.Add(detail);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        if (issue.CanAutoFix && !isResolved)
        {
            var fixButton = new Wpf.Ui.Controls.Button
            {
                Content = Locale.Get("Diag.Button.Fix"),
                Appearance = ControlAppearance.Primary,
                MinWidth = 72,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 0, 0)
            };
            fixButton.Click += async (_, _) => await RunFixAsync(issue, fixButton);
            Grid.SetColumn(fixButton, 1);
            row.Children.Add(fixButton);
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
            Cursor = Cursors.Hand
        };

        border.MouseLeftButtonUp += (_, _) =>
        {
            detail.Visibility = detail.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };

        return border;
    }

    private Border BuildResolvedRow(string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "✓",
            Foreground = new SolidColorBrush(ResolvedColor),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 8, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, ResolvedColor.R, ResolvedColor.G, ResolvedColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, ResolvedColor.R, ResolvedColor.G, ResolvedColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = panel
        };
    }

    /// <summary>
    /// 执行一键修复。
    ///
    /// 改系统设置前必须让用户确认——尤其是「打开远程桌面」和「还原 TermService」这两项，
    /// 它们会改变安全边界或让另一个功能失效。
    /// </summary>
    private async Task RunFixAsync(ChildSessionEnvironmentCheck.Issue issue, Wpf.Ui.Controls.Button button)
    {
        if (_fixInProgress)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            Locale.Format("Diag.Confirm.Fix", issue.Title, BuildConfirmHint(issue.Id)),
            Locale.Get("Diag.Confirm.FixButton"));

        if (!confirmed)
        {
            return;
        }

        _fixInProgress = true;
        button.IsEnabled = false;
        _statusText.Text = Locale.Get("Diag.Status.Fixing");

        try
        {
            var result = await EnvironmentFixer.FixAsync(issue.Id, CancellationToken.None);
            _statusText.Text = result.Message;
            AppLog.Info($"诊断修复 {issue.Id}：{(result.Success ? "成功" : "失败")}，{result.Message}");
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            _statusText.Text = Locale.Format("Diag.Status.FixFailed", exception.GetBaseException().Message);
            AppLog.Warn($"诊断修复 {issue.Id} 抛出异常。", exception);
        }
        finally
        {
            _fixInProgress = false;
            if (!_closed)
            {
                button.IsEnabled = true;
                Refresh();
            }
        }
    }

    private static string BuildConfirmHint(string issueId) => issueId switch
    {
        "rdp-disabled" => Locale.Get("Diag.FixHint.RdpDisabled"),
        "rdp-wrapper" => Locale.Get("Diag.FixHint.RdpWrapper"),
        "termservice-disabled" => Locale.Get("Diag.FixHint.TermServiceDisabled"),
        "termservice-stopped" => Locale.Get("Diag.FixHint.TermServiceStopped"),
        "hello-only" => Locale.Get("Diag.FixHint.HelloOnly"),
        _ => Locale.Get("Diag.FixHint.Default")
    };

    private async Task RestartComputerAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            Locale.Get("Diag.Reboot.Message"),
            Locale.Get("Diag.Button.Reboot"),
            Locale.Get("Diag.Button.RebootNow"));

        if (!confirmed)
        {
            return;
        }

        var shutdown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "shutdown.exe");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = shutdown,
                Arguments = "/r /t 5 /c \"" + Locale.Get("Diag.Reboot.Comment") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            _statusText.Text = Locale.Get("Diag.Status.RebootRequested");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            _statusText.Text = Locale.Format("Diag.Status.RebootFailed", exception.GetBaseException().Message);
        }
    }
}
