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
    private static readonly Color InactiveColor = Color.FromRgb(0x9A, 0x9A, 0x9A);

    private readonly StackPanel _warningSection;
    private readonly StackPanel _noticeSection;
    private readonly StackPanel _resolvedSection;

    /// <summary>未触发的检查项，只在「全部检查项」视图里有内容。</summary>
    private readonly StackPanel _inactiveSection;

    /// <summary>是否显示未触发的检查项。按 Shift 打开时为 true。</summary>
    private readonly bool _showAll;

    /// <summary>是否补一条「密码可能不正确」的运行时结论。</summary>
    private bool _credentialHint;

    /// <summary>最近一次渲染的结论，供「反馈问题」拼标题用。</summary>
    private IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _lastIssues = [];

    /// <summary>「启动失败」横幅：说明这次是被启动失败带进来的。</summary>
    private readonly StackPanel _startupBanner;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>出现过的所有问题标识，用于判断「曾经有问题、现在没了」。</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>已经解决（当前检测不到）的问题标识。</summary>
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);

    /// <summary>问题标题快照，解决后仍然能显示原来的标题。</summary>
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    /// <summary>当前处于展开状态的行（按问题标识）。刷新重建时按它恢复。</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>上一次渲染的内容签名。内容没变就不重建，避免每 2 秒把展开的行折回去。</summary>
    private string? _renderedSignature;

    private bool _fixInProgress;
    private bool _closed;

    /// <summary>
    /// 创建面板。
    /// </summary>
    public DiagnosticsWindow()
        : this(showAll: false)
    {
    }

    /// <summary>
    /// 创建面板。
    /// </summary>
    /// <param name="showAll">
    /// true 时额外列出当前没有触发的检查项。用于核对文案（含翻译），
    /// 由「按住 Shift 打开诊断与修复」触发。
    /// </param>
    /// <param name="startupFailure">
    /// true 时在顶部显示「启动失败」横幅——面板是被启动失败带出来的，而不是用户主动打开的。
    /// </param>
    /// <param name="credentialHint">
    /// true 时补一条「密码可能不正确」的运行时结论。只有「刚连接失败」的现场才该带上：
    /// 它描述的是上一次尝试，不属于环境状态。
    /// </param>
    public DiagnosticsWindow(bool showAll, bool startupFailure = false, bool credentialHint = false)
    {
        _showAll = showAll;
        _credentialHint = credentialHint;

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
        _inactiveSection = CreateSection();

        // 「启动失败」时才显示的一行标题：说明这次不是用户主动打开的面板。
        _startupBanner = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = startupFailure ? Visibility.Visible : Visibility.Collapsed
        };
        _startupBanner.Children.Add(new TextBlock
        {
            Text = Locale.Get("Diag.StartupFailed.Title"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WarningColor),
            TextWrapping = TextWrapping.Wrap
        });

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

    /// <summary>
    /// 把已打开的面板标成「启动失败」：显示顶部横幅并置前。
    /// 用户先手动打开过面板、随后点启动被拒时会走到这里。
    /// </summary>
    internal void MarkStartupFailure()
    {
        _startupBanner.Visibility = Visibility.Visible;
        Activate();
    }

    /// <summary>
    /// 打开 / 收起「密码可能不正确」这条运行时结论，并立即重绘。
    /// 用户在这里重设密码成功后调用 false，让那条提示跟着消失。
    /// </summary>
    internal void SetCredentialHint(bool value)
    {
        if (_credentialHint == value)
        {
            return;
        }

        _credentialHint = value;
        Refresh();
    }

    /// <summary>
    /// 「用自带自动登录修复」被点击。
    ///
    /// 这一项不是注册表修复，需要弹密码框、改设置里的开关，都是 UI 动作，
    /// 因此交给宿主处理；面板只负责发起。
    /// </summary>
    internal event EventHandler? PasswordSetupRequested;

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_startupBanner);
        header.Children.Add(new TextBlock
        {
            Text = Locale.Get("Diag.Intro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });
        header.Children.Add(new TextBlock
        {
            Text = _showAll ? Locale.Get("Diag.ShowAll.Hint") : Locale.Get("Diag.Hint"),
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

        if (_showAll)
        {
            sections.Children.Add(BuildSectionHeader(Locale.Get("Diag.Section.Inactive"), InactiveColor));
            sections.Children.Add(_inactiveSection);
        }

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
        // 用户主动点「重新检测」：缓存一律作废。否则 termsrv.dll 的 5 分钟结论会让
        // 用户在别处修好的文件仍显示旧警告，还带一个点了也修不动的「修复」按钮。
        refreshButton.Click += (_, _) =>
        {
            ChildSessionEnvironmentCheck.InvalidateScanCache();
            Refresh();
        };

        var restartButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Diag.Button.Reboot"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 92
        };
        restartButton.Click += async (_, _) => await RestartComputerAsync();

        var feedbackButton = new Wpf.Ui.Controls.Button
        {
            Content = Locale.Get("Diag.Button.Feedback"),
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = Locale.Get("Diag.Button.Feedback.ToolTip")
        };
        feedbackButton.Click += (_, _) => OpenFeedbackPage();

        buttons.Children.Add(closeButton);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(feedbackButton);
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
            issues = _showAll
                ? ChildSessionEnvironmentCheck.CollectAll(_credentialHint)
                : ChildSessionEnvironmentCheck.Collect(_credentialHint);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            _statusText.Text = Locale.Format("Diag.Status.CheckFailed", exception.GetBaseException().Message);
            return;
        }

        var currentIds = new HashSet<string>(StringComparer.Ordinal);

        // 留一份给「反馈问题」：标题直接带上当前最主要的问题，省得用户再描述一遍。
        _lastIssues = issues;

        foreach (var issue in issues)
        {
            if (issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Inactive)
            {
                // 未触发项不参与「已解决」判断，否则一切正常时它们会全被记成已解决过。
                continue;
            }

            currentIds.Add(issue.Id);
            _titles[issue.Id] = issue.Title;
            _seen.Add(issue.Id);

            // 又出现了，说明之前的「已解决」判断作废。
            _resolved.Remove(issue.Id);
        }

        // 校验还没出结论时，termsrv 那一条会暂时不出现：这时不能把它记成「已解决」，
        // 否则界面会先打勾、几秒后又变回问题。
        var termsrvPending = ChildSessionEnvironmentCheck.IsTermsrvVerifying;
        var termsrvState = ChildSessionEnvironmentCheck.GetTermsrvState();
        var termsrvConclusive = termsrvState.Exists && termsrvState.Verified && termsrvState.Intact;
        foreach (var id in _seen)
        {
            if (currentIds.Contains(id))
            {
                continue;
            }

            // 未出结论（校验中、没提权、sfc 跑不起来）都不算「已解决」：
            // 只有确认文件完好才允许打勾，否则会先打勾、下次又变回问题。
            if ((termsrvPending || !termsrvConclusive)
                && string.Equals(id, "termsrv-dll-tampered", StringComparison.Ordinal))
            {
                continue;
            }

            _resolved.Add(id);
        }

        var warningCount = 0;
        var noticeCount = 0;
        var inactiveCount = 0;

        // 先只统计，不急着动界面。
        foreach (var issue in issues)
        {
            switch (issue.IssueSeverity)
            {
                case ChildSessionEnvironmentCheck.Severity.Warning:
                    warningCount++;
                    break;

                case ChildSessionEnvironmentCheck.Severity.Inactive:
                    inactiveCount++;
                    break;

                default:
                    noticeCount++;
                    break;
            }
        }

        // 2 秒一次轮询，但环境通常几小时都不变。内容没变就只更新状态栏，
        // 否则用户刚点开的说明会被下一次重建折回去（滚动位置也会跳回顶部）。
        var signature = BuildRenderSignature(issues);
        if (signature == _renderedSignature)
        {
            UpdateStatus(warningCount, noticeCount, inactiveCount);
            return;
        }

        _renderedSignature = signature;

        _warningSection.Children.Clear();
        _noticeSection.Children.Clear();
        _resolvedSection.Children.Clear();
        _inactiveSection.Children.Clear();

        foreach (var issue in issues)
        {
            switch (issue.IssueSeverity)
            {
                case ChildSessionEnvironmentCheck.Severity.Warning:
                    _warningSection.Children.Add(BuildRow(issue, isResolved: false));
                    break;

                case ChildSessionEnvironmentCheck.Severity.Inactive:
                    _inactiveSection.Children.Add(BuildRow(issue, isResolved: false));
                    break;

                default:
                    _noticeSection.Children.Add(BuildRow(issue, isResolved: false));
                    break;
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

        if (_showAll && inactiveCount == 0)
        {
            _inactiveSection.Children.Add(BuildPlaceholder(Locale.Get("Diag.Empty.Inactive")));
        }

        UpdateStatus(warningCount, noticeCount, inactiveCount);
    }

    /// <summary>
    /// 刷新底部状态栏。计数变了但列表没变时也要更新，所以单独拆出来。
    /// </summary>
    private void UpdateStatus(int warningCount, int noticeCount, int inactiveCount)
    {
        if (_fixInProgress)
        {
            return;
        }

        _statusText.Text = _showAll
            ? Locale.Format("Diag.Status.SummaryAll", warningCount + noticeCount, inactiveCount)
            : warningCount == 0
                ? Locale.Get("Diag.Status.Clean")
                : Locale.Format("Diag.Status.Summary", warningCount, noticeCount);
    }

    /// <summary>
    /// 当前这一屏的内容签名。用它判断「和上次渲染的一样吗」。
    ///
    /// 刻意把文案也纳入签名：换了语言文件、或者某一项的当前值变了（未触发项会显示当前值），
    /// 都要重建，否则界面会停在旧文字上。
    /// </summary>
    private string BuildRenderSignature(IReadOnlyList<ChildSessionEnvironmentCheck.Issue> issues)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(_showAll ? '1' : '0');

        foreach (var issue in issues)
        {
            builder.Append('\u0001')
                .Append(issue.Id).Append('|')
                .Append((int)issue.IssueSeverity).Append('|')
                .Append(issue.CanAutoFix ? '1' : '0').Append('|')
                .Append(issue.Title).Append('|')
                .Append(issue.Summary).Append('|')
                .Append(issue.Detail);
        }

        // 已解决区只显示标题，按标识排序保证顺序稳定。
        var resolved = new List<string>(_resolved);
        resolved.Sort(StringComparer.Ordinal);
        foreach (var id in resolved)
        {
            builder.Append('\u0002')
                .Append(id).Append('|')
                .Append(_titles.TryGetValue(id, out var title) ? title : id);
        }

        return builder.ToString();
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

            // 展开状态存在 _expanded 里：列表重建（例如环境真的变了）之后仍然展开。
            Visibility = _expanded.Contains(issue.Id) ? Visibility.Visible : Visibility.Collapsed
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
                // 这一项的动作是「设置密码 + 打开自动登录」，写成「修复」太含糊。
                Content = issue.Id == "credential-rejected"
                    ? Locale.Get("Diag.Button.SetPassword")
                    : Locale.Get("Diag.Button.Fix"),
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
            if (detail.Visibility == Visibility.Visible)
            {
                detail.Visibility = Visibility.Collapsed;
                _expanded.Remove(issue.Id);
            }
            else
            {
                detail.Visibility = Visibility.Visible;
                _expanded.Add(issue.Id);
            }
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

        // 「用自带自动登录修复」不碰注册表：打开密码设置窗口由宿主完成。
        // 也因此不需要「会修改系统设置」那个确认框，直接交给用户输入密码即可。
        if (issue.Id == "credential-rejected")
        {
            _statusText.Text = Locale.Get("Diag.Status.PasswordSetup");
            PasswordSetupRequested?.Invoke(this, EventArgs.Empty);
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
        "termservice-dll-type" => Locale.Get("Diag.FixHint.DllType"),
        "termservice-key-broken" => Locale.Get("Diag.FixHint.ServiceKey"),
        "rdp-tool-persistence" => Locale.Get("Diag.FixHint.Persistence"),
        "rdp-security-downgraded" => Locale.Get("Diag.FixHint.RdpSecurity"),
        "termsrv-dll-tampered" => Locale.Get("Diag.FixHint.Termsrv"),
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

    /// <summary>
    /// 打开 GitHub 的新建 Issue 页面，并顺手把环境快照放进剪贴板。
    ///
    /// 仓库用的是 issue form（带表单字段的 yml 模板），正文没法用 URL 预填，
    /// 所以这里只预填标题；用户到了页面把快照粘进「环境信息」一栏即可。
    /// 先复制再开页面：万一浏览器没起来，剪贴板里至少还有能贴的内容。
    /// </summary>
    private void OpenFeedbackPage()
    {
        var copied = true;
        try
        {
            Clipboard.SetText(ChildSessionEnvironmentCheck.BuildDiagnosticSnapshot());
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException
                                              or InvalidOperationException)
        {
            // 剪贴板被别的程序占着不算致命：页面照样打开，用户还能从菜单里再复制一次。
            copied = false;
            AppLog.Warn("复制环境信息失败，继续打开反馈页面。", exception);
        }

        var title = $"[Bug] {BuildFeedbackTitle()}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = AppLinks.NewIssue(title),
                UseShellExecute = true
            });

            _statusText.Text = Locale.Get(copied
                ? "Diag.Status.FeedbackOpened"
                : "Diag.Status.FeedbackOpenedNoCopy");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            _statusText.Text = Locale.Format("Diag.Status.FeedbackFailed", AppLinks.Issues);
        }
    }

    /// <summary>
    /// 反馈标题里带的问题名：优先取当前触发的警告，没有就用提示，都没有就写启动失败。
    /// </summary>
    private string BuildFeedbackTitle()
    {
        string? firstNotice = null;

        foreach (var issue in _lastIssues)
        {
            if (issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Warning)
            {
                return issue.Title;
            }

            if (issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Notice && firstNotice is null)
            {
                firstNotice = issue.Title;
            }
        }

        return firstNotice ?? Locale.Get("Diag.Feedback.DefaultTitle");
    }
}
