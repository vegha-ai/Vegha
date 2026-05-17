using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Shell;

public partial class RequestTabStrip : UserControl
{
    /// <summary>Pixels to advance per click of the left/right scroll arrows. Larger than one
    /// tab so consecutive clicks visibly jump rather than nudge.</summary>
    private const double ScrollStep = 160;

    private OpenTabsViewModel? _attached;

    public RequestTabStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => UpdateScrollArrows();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attached is not null) _attached.PropertyChanged -= OnTabsPropertyChanged;
        _attached = DataContext as OpenTabsViewModel;
        if (_attached is not null) _attached.PropertyChanged += OnTabsPropertyChanged;
        // Initial scroll-arrow + active-tab sync once the bindings have settled.
        Dispatcher.UIThread.Post(() => { UpdateScrollArrows(); ScrollActiveTabIntoView(); }, DispatcherPriority.Background);
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When the active tab changes (e.g. user clicks a request in the tree), bring its
        // tab header into view so it isn't hidden off the right edge after opening.
        if (e.PropertyName == nameof(OpenTabsViewModel.ActiveTab))
            Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
    }

    private void OnTabsScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateScrollArrows();

    /// <summary>Enable / disable the left + right arrow buttons based on whether there's
    /// content beyond the viewport in that direction. Both arrows stay rendered (so the strip
    /// layout doesn't jitter when overflow appears) but go semi-transparent + disabled when
    /// they have nothing to do.</summary>
    private void UpdateScrollArrows()
    {
        if (TabsScrollViewer is null) return;
        var offset = TabsScrollViewer.Offset.X;
        var extent = TabsScrollViewer.Extent.Width;
        var viewport = TabsScrollViewer.Viewport.Width;
        var canScrollLeft = offset > 0.5;
        var canScrollRight = offset + viewport < extent - 0.5;
        if (ScrollLeftButton is not null) ScrollLeftButton.IsEnabled = canScrollLeft;
        if (ScrollRightButton is not null) ScrollRightButton.IsEnabled = canScrollRight;
    }

    private void OnScrollLeft_Click(object? sender, RoutedEventArgs e) =>
        ScrollBy(-ScrollStep);

    private void OnScrollRight_Click(object? sender, RoutedEventArgs e) =>
        ScrollBy(ScrollStep);

    private void ScrollBy(double delta)
    {
        if (TabsScrollViewer is null) return;
        var current = TabsScrollViewer.Offset.X;
        var maxOffset = Math.Max(0, TabsScrollViewer.Extent.Width - TabsScrollViewer.Viewport.Width);
        var next = Math.Clamp(current + delta, 0, maxOffset);
        TabsScrollViewer.Offset = new Vector(next, TabsScrollViewer.Offset.Y);
    }

    /// <summary>Scrolls so the active tab is fully inside the viewport. Used when the user
    /// opens a request from the tree — without this, the new tab might land off-screen if
    /// many tabs are already open.</summary>
    private void ScrollActiveTabIntoView()
    {
        if (_attached is null || _attached.ActiveTab is null) return;
        if (TabsScrollViewer is null || TabsItemsControl is null) return;
        var container = TabsItemsControl.ContainerFromItem(_attached.ActiveTab) as Control;
        if (container is null) return;

        // BringIntoView handles the math (including when the tab is wider than the
        // viewport — it aligns the leading edge). It walks ancestor ScrollViewers and
        // adjusts their offsets.
        container.BringIntoView();
    }

    private void OnTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not OpenTabsViewModel tabs) return;
        if (sender is not Border border || border.Tag is not RequestTabViewModel tab) return;

        // Middle-click closes; left-click activates.
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            tabs.CloseTab(tab);
            e.Handled = true;
            return;
        }
        tabs.ActiveTab = tab;
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OpenTabsViewModel tabs) return;
        if (sender is not Button btn || btn.Tag is not RequestTabViewModel tab) return;
        tabs.CloseTab(tab);
        e.Handled = true;
    }

    private void OnNewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OpenTabsViewModel tabs) tabs.OpenDraft();
    }
}
