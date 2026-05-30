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

    /// <summary>Raised by the "+" button and the tab menu's "New Request" entry. The host creates
    /// a fresh scratch request (it owns the file IO + scratch store).</summary>
    public event EventHandler? NewRequestRequested;

    /// <summary>Raised by the tab menu's "Clone Request" entry — host duplicates the request into
    /// a new scratch tab.</summary>
    public event EventHandler<RequestTabViewModel>? CloneRequested;

    /// <summary>Raised by the tab menu's "Rename" entry — host prompts for a name and renames the
    /// backing file.</summary>
    public event EventHandler<RequestTabViewModel>? RenameRequested;

    /// <summary>Raised by the tab menu's "Revert Changes" entry — host reloads the request from
    /// disk, discarding unsaved edits.</summary>
    public event EventHandler<RequestTabViewModel>? RevertRequested;

    /// <summary>Raised by the tab menu's "Save to collection…" entry — host promotes a scratch
    /// request into a real collection.</summary>
    public event EventHandler<RequestTabViewModel>? SaveToCollectionRequested;

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

    private void OnNewTab_Click(object? sender, RoutedEventArgs e) =>
        NewRequestRequested?.Invoke(this, EventArgs.Empty);

    // ---- Per-tab right-click context menu ----
    // The MenuFlyout is attached to each tab Border, so its items inherit that Border's
    // DataContext (the RequestTabViewModel). Handlers read the tab from the MenuItem's Tag
    // (bound to the same {Binding}), mirroring the close button's existing pattern.

    private static RequestTabViewModel? TabFrom(object? sender) =>
        (sender as Control)?.Tag as RequestTabViewModel;

    private void OnMenuNewRequest_Click(object? sender, RoutedEventArgs e) =>
        NewRequestRequested?.Invoke(this, EventArgs.Empty);

    private void OnMenuClone_Click(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) CloneRequested?.Invoke(this, tab);
    }

    private void OnMenuRename_Click(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) RenameRequested?.Invoke(this, tab);
    }

    private void OnMenuRevert_Click(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) RevertRequested?.Invoke(this, tab);
    }

    private void OnMenuSaveToCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) SaveToCollectionRequested?.Invoke(this, tab);
    }

    private void OnMenuClose_Click(object? sender, RoutedEventArgs e)
    {
        if (_attached is { } tabs && TabFrom(sender) is { } tab) tabs.CloseTab(tab);
    }

    private void OnMenuCloseOthers_Click(object? sender, RoutedEventArgs e)
    {
        if (_attached is { } tabs && TabFrom(sender) is { } tab) tabs.CloseOthers(tab);
    }

    private void OnMenuCloseLeft_Click(object? sender, RoutedEventArgs e)
    {
        if (_attached is { } tabs && TabFrom(sender) is { } tab) tabs.CloseToLeft(tab);
    }

    private void OnMenuCloseRight_Click(object? sender, RoutedEventArgs e)
    {
        if (_attached is { } tabs && TabFrom(sender) is { } tab) tabs.CloseToRight(tab);
    }

    private void OnMenuCloseSaved_Click(object? sender, RoutedEventArgs e) =>
        _attached?.CloseSaved();

    private void OnMenuCloseAll_Click(object? sender, RoutedEventArgs e) =>
        _attached?.CloseAll();
}
