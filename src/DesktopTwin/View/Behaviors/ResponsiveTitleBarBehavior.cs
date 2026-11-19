using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;
using Wpf.Ui.Controls;

namespace DesktopTwin.View.Behaviors;

public sealed class ResponsiveTitleBarBehavior : Behavior<Panel>
{
    public static readonly DependencyProperty CollapseOrderProperty =
        DependencyProperty.RegisterAttached(
            "CollapseOrder",
            typeof(int),
            typeof(ResponsiveTitleBarBehavior),
            new PropertyMetadata(0));

    public static readonly DependencyProperty MinimumGapProperty =
        DependencyProperty.Register(
            nameof(MinimumGap),
            typeof(double),
            typeof(ResponsiveTitleBarBehavior),
            new PropertyMetadata(8d));

    private readonly Dictionary<FrameworkElement, double> _elementWidths = new();
    private Window? _window;
    private TitleBar? _titleBar;
    private double? _rightReservedWidth;
    private double? _headerRight;
    private bool _updatePending;

    public double MinimumGap
    {
        get => (double)GetValue(MinimumGapProperty);
        set => SetValue(MinimumGapProperty, value);
    }

    public static int GetCollapseOrder(DependencyObject obj)
    {
        return (int)obj.GetValue(CollapseOrderProperty);
    }

    public static void SetCollapseOrder(DependencyObject obj, int value)
    {
        obj.SetValue(CollapseOrderProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded += OnAssociatedObjectUnloaded;

        if (AssociatedObject.IsLoaded)
        {
            Attach();
        }
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded -= OnAssociatedObjectUnloaded;
        DetachTitleBar();
        base.OnDetaching();
    }

    private void OnAssociatedObjectLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
    }

    private void OnAssociatedObjectUnloaded(object sender, RoutedEventArgs e)
    {
        DetachTitleBar();
    }

    private void Attach()
    {
        if (_window is not null)
        {
            return;
        }

        _window = Window.GetWindow(AssociatedObject);
        _titleBar = FindVisualAncestor<TitleBar>(AssociatedObject);
        if (_window is null || _titleBar is null)
        {
            _window = null;
            _titleBar = null;
            return;
        }

        CacheElementWidths();
        _window.SizeChanged += OnWindowSizeChanged;
        _window.Closed += OnWindowClosed;
        ScheduleUpdate();
    }

    private void DetachTitleBar()
    {
        if (_window is not null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
            _window.Closed -= OnWindowClosed;
        }

        _window = null;
        _titleBar = null;
        _rightReservedWidth = null;
        _headerRight = null;
        _elementWidths.Clear();
        _updatePending = false;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleUpdate();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DetachTitleBar();
    }

    private void ScheduleUpdate()
    {
        if (_updatePending)
        {
            return;
        }

        _updatePending = true;
        _ = AssociatedObject.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _updatePending = false;
                UpdateChildVisibility();
            }));
    }

    private void CacheElementWidths()
    {
        foreach (var element in AssociatedObject.Children.OfType<FrameworkElement>())
        {
            var width = double.IsNaN(element.Width)
                ? element.DesiredSize.Width
                : element.Width;
            _elementWidths[element] = Math.Max(0, width + element.Margin.Left + element.Margin.Right);
        }
    }

    private void UpdateChildVisibility()
    {
        if (_titleBar is null || !_titleBar.IsVisible)
        {
            return;
        }

        var collapsibleElements = AssociatedObject.Children
            .OfType<FrameworkElement>()
            .Where(element => GetCollapseOrder(element) > 0)
            .OrderBy(GetCollapseOrder)
            .ToArray();
        if (collapsibleElements.Length == 0)
        {
            return;
        }

        CacheMissingElementWidths();
        var availableWidth = CalculateAvailableWidth();
        // 需求按「每个按钮都可能显示」算，包括此刻因状态而隐藏的：
        // 例如「更多」下拉在启动按钮可见时是收起的，但一折叠启动按钮它就会露出来。
        // 漏算这几十 DIP，后面的兜底判断会以为放得下，按钮就被系统按钮盖住了。
        //
        // 反过来「都算」会多折一个按钮，但那是可接受的方向：少显示一个按钮还能从
        // 「更多」下拉找到，被系统按钮盖住则直接点不到。
        var requiredWidth = AssociatedObject.Children
            .OfType<FrameworkElement>()
            .Sum(GetCachedWidth);
        var collapsedElements = new HashSet<FrameworkElement>();

        foreach (var element in collapsibleElements)
        {
            if (requiredWidth <= availableWidth)
            {
                break;
            }

            collapsedElements.Add(element);
            requiredWidth -= GetCachedWidth(element);
        }

        foreach (var element in collapsibleElements)
        {
            element.SetCurrentValue(
                UIElement.VisibilityProperty,
                collapsedElements.Contains(element) ? Visibility.Collapsed : Visibility.Visible);
        }

        // 兜底：全部折叠完还是放不下（窗口被拖到极窄、或高 DPI 缩放后系统按钮
        // 占走更多宽度）。这时候不能再让内容往外挤——TitleBar 模板里的最小化/
        // 最大化/关闭会被顶出可视区，用户连关窗都点不到。
        // 把宽度夹到可用范围、超出部分裁掉：宁可少显示一个按钮，也不能弄丢关闭按钮。
        var stillOverflows = requiredWidth > availableWidth;
        AssociatedObject.ClipToBounds = stillOverflows;
        AssociatedObject.SetCurrentValue(
            FrameworkElement.MaxWidthProperty,
            stillOverflows ? availableWidth : double.PositiveInfinity);
    }

    private void CacheMissingElementWidths()
    {
        foreach (var element in AssociatedObject.Children.OfType<FrameworkElement>())
        {
            if (_elementWidths.ContainsKey(element))
            {
                continue;
            }

            var width = double.IsNaN(element.Width)
                ? element.DesiredSize.Width
                : element.Width;
            _elementWidths[element] = Math.Max(0, width + element.Margin.Left + element.Margin.Right);
        }
    }

    private double CalculateAvailableWidth()
    {
        if (_titleBar is null)
        {
            return 0;
        }

        var panelRight = AssociatedObject
            .TranslatePoint(new Point(AssociatedObject.ActualWidth, 0), _titleBar)
            .X;
        var rightReservedWidth = _titleBar.ActualWidth - panelRight;

        // 取历史最大值，而不是「第一次测到的值」：内容溢出时 TitleBar 会把
        // 右边这一块压小，此时测到的是挤压结果、不是系统按钮的真实占宽。
        // 用它去算可用宽度会越算越大，该折叠的按钮就不折叠了。
        // 布局挤压只会让这个数字变小，所以最大值才是真实占宽的下界。
        if (rightReservedWidth > (_rightReservedWidth ?? 0))
        {
            _rightReservedWidth = rightReservedWidth;
        }

        var headerRight = _titleBar.Header is FrameworkElement header
            ? header.TranslatePoint(new Point(header.ActualWidth, 0), _titleBar).X
            : 0;

        // 扣掉标题区（图标 + 标题）本身占的宽度：WPF-UI 的 TitleBar 模板里 Header
        // 是 Auto 列，它按内容宽度占位、不会自动让位给按钮。不预留这块空间的话，
        // 算出来的可用宽度偏大，按钮会保持展开、把标题和系统按钮一起顶出窗口。
        //
        // 取历史最大值：内容溢出时这块会被挤压，测到的偏小，用它算会越算越大。
        if (headerRight > (_headerRight ?? 0))
        {
            _headerRight = headerRight;
        }

        return Math.Max(
            0,
            _titleBar.ActualWidth -
            Math.Max(0, _rightReservedWidth ?? rightReservedWidth) -
            Math.Max(0, _headerRight ?? headerRight) -
            Math.Max(0, MinimumGap));
    }

    private double GetCachedWidth(FrameworkElement element)
    {
        return _elementWidths.GetValueOrDefault(element);
    }

    private static T? FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T ancestor)
            {
                return ancestor;
            }
        }

        return null;
    }
}
