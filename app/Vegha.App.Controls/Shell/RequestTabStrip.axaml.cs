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

    /// <summary>Width of either "+" button (they're interchangeable, see
    /// <see cref="UpdateOverflowChrome"/>). The overflow test reserves this much so the
    /// inline "+" is never the thing that pushes the strip into scrolling.</summary>
    private const double NewTabButtonWidth = 30;

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
        AttachedToVisualTree += (_, _) => UpdateOverflowChrome();
        // Overflow depends on two things only: how wide the strip is, and how wide the
        // tab run is. Watching both directly (rather than LayoutUpdated) keeps the
        // recompute off the app-wide layout hot path while still catching every cause —
        // window resize, sidebar toggle, tab opened/closed, tab renamed.
        StripRoot.SizeChanged += (_, _) => UpdateOverflowChrome();
        TabsItemsControl.SizeChanged += (_, _) => UpdateOverflowChrome();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attached is not null) _attached.PropertyChanged -= OnTabsPropertyChanged;
        _attached = DataContext as OpenTabsViewModel;
        if (_attached is not null) _attached.PropertyChanged += OnTabsPropertyChanged;
        // Initial scroll-arrow + active-tab sync once the bindings have settled.
        Dispatcher.UIThread.Post(() => { UpdateOverflowChrome(); ScrollActiveTabIntoView(); }, DispatcherPriority.Background);
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When the active tab changes (e.g. user clicks a request in the tree), bring its
        // tab header into view so it isn't hidden off the right edge after opening.
        if (e.PropertyName == nameof(OpenTabsViewModel.ActiveTab))
            Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
    }

    private void OnTabsScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateOverflowChrome();

    /// <summary>Decides which overflow chrome the strip shows: the scroll arrows, and which
    /// of the two "+" buttons (inline-after-the-last-tab vs. pinned-far-right).
    ///
    /// The overflow test deliberately uses the tab run's own width against the strip width
    /// — <em>not</em> the ScrollViewer's extent-vs-viewport — because extent and viewport
    /// both move when we toggle the chrome, which would make the decision feed back into
    /// itself and flip-flop across layout passes. Tab-run width and strip width are
    /// independent of everything this method touches, so the result is stable.
    ///
    /// A "+" width is always reserved, so the inline "+" itself is never what tips the strip
    /// into scrolling. The arrows' width is only subtracted once we've already decided the
    /// strip overflows, for the same no-feedback reason.</summary>
    private void UpdateOverflowChrome()
    {
        if (TabsScrollViewer is null || TabsItemsControl is null || StripRoot is null) return;

        var stripWidth = StripRoot.Bounds.Width;
        var tabsWidth = TabsItemsControl.Bounds.Width;
        // Before the first real layout pass everything is zero — leave the chrome alone
        // rather than briefly flashing the pinned "+".
        if (stripWidth <= 0) return;

        var overflows = tabsWidth > stripWidth - NewTabButtonWidth + 0.5;

        if (NewTabButton is not null) NewTabButton.IsVisible = !overflows;
        if (PinnedNewTabButton is not null) PinnedNewTabButton.IsVisible = overflows;

        // While overflowing, each arrow additionally enables/disables based on whether
        // there's content beyond the viewport on its side. With few (or no) tabs the
        // arrows hide entirely — dimmed chevrons over an empty gray band read as broken
        // chrome.
        var offset = TabsScrollViewer.Offset.X;
        var extent = TabsScrollViewer.Extent.Width;
        var viewport = TabsScrollViewer.Viewport.Width;
        if (ScrollLeftButton is not null)
        {
            ScrollLeftButton.IsVisible = overflows;
            ScrollLeftButton.IsEnabled = offset > 0.5;
        }
        if (ScrollRightButton is not null)
        {
            ScrollRightButton.IsVisible = overflows;
            ScrollRightButton.IsEnabled = offset + viewport < extent - 0.5;
        }
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
