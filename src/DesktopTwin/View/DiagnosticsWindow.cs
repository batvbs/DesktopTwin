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
        Title = "诊断与修复";
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
            Text = "正在检查…",
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
            Text = "桌面分身依赖 Windows 的远程桌面与子会话能力。下面列出当前环境里可能影响启动的问题。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });
        header.Children.Add(new TextBlock
        {
            Text = "点任意一行可展开完整说明与手动修复步骤；能自动修复的会在右侧显示「修复」按钮。",
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
        sections.Children.Add(BuildSectionHeader("警告", WarningColor));
        sections.Children.Add(_warningSection);
        sections.Children.Add(BuildSectionHeader("提示", NoticeColor));
        sections.Children.Add(_noticeSection);
        sections.Children.Add(BuildSectionHeader("已解决", ResolvedColor));
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

        var refreshButton = new Wpf.Ui.Controls.Button
        {
            Content = "重新检测",
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0)
        };
        refreshButton.Click += (_, _) => Refresh();

        var restartButton = new Wpf.Ui.Controls.Button
        {
            Content = "重启电脑",
            Appearance = ControlAppearance.Primary,
            MinWidth = 92
        };
        restartButton.Click += async (_, _) => await RestartComputerAsync();

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
            _statusText.Text = $"检查失败：{exception.GetBaseException().Message}";
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
            _warningSection.Children.Add(BuildPlaceholder("没有发现会阻止启动的问题。"));
        }

        if (noticeCount == 0)
        {
            _noticeSection.Children.Add(BuildPlaceholder("没有提示项。"));
        }

        foreach (var id in _resolved)
        {
            _resolvedSection.Children.Add(BuildResolvedRow(_titles.TryGetValue(id, out var title) ? title : id));
        }

        if (_resolved.Count == 0)
        {
            _resolvedSection.Children.Add(BuildPlaceholder("还没有已解决的问题。修好之后会自动移到这里。"));
        }

        if (!_fixInProgress)
        {
            _statusText.Text = warningCount == 0
                ? "环境检查完成：没有阻塞项。"
                : $"环境检查完成：{warningCount} 个警告，{noticeCount} 个提示。";
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
                Content = "修复",
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
            $"即将修复：{issue.Title}\n\n"
            + BuildConfirmHint(issue.Id)
            + "\n\n是否继续？",
            "确认修复");

        if (!confirmed)
        {
            return;
        }

        _fixInProgress = true;
        button.IsEnabled = false;
        _statusText.Text = "正在修复…";

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
            _statusText.Text = $"修复失败：{exception.GetBaseException().Message}";
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
        "rdp-disabled" => "这会开启本机的远程桌面监听（允许他人从网络连接这台电脑），"
            + "并重启远程桌面服务。如果此刻有分身或远程会话在运行，修复会被拒绝。",
        "rdp-wrapper" => "这会把 TermService 还原成系统原生的 termsrv.dll，并停用补丁的自动更新任务。"
            + "还原后，多用户并发远程桌面的能力会失效，需要重装补丁工具才能恢复。",
        "termservice-disabled" => "这会把远程桌面服务的启动类型从「禁用」改回「手动」并启动它。",
        "termservice-stopped" => "这会启动远程桌面服务。",
        "hello-only" => "这会关闭「仅允许 Windows Hello 登录」。改完需要你自己注销或重启才会生效。",
        _ => "这会修改系统设置。"
    };

    private async Task RestartComputerAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            "将立即重启这台电脑。\n\n"
            + "请先保存所有正在编辑的内容——重启会关闭本机所有程序，"
            + "分身里的程序也会一起结束。\n\n确定要重启吗？",
            "重启电脑",
            "立即重启");

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
                Arguments = "/r /t 5 /c \"桌面分身：诊断修复后重启\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            _statusText.Text = "已请求重启，5 秒后关机。";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            _statusText.Text = $"无法发起重启：{exception.GetBaseException().Message}。请手动重启。";
        }
    }
}
