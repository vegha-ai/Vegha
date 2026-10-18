using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;

#nullable enable

namespace Vegha.App.Controls.Shell;

public partial class CollectionsPanel : UserControl
{
    /// <summary>Raised when the user picks "Import collection" from the "+" flyout. The host
    /// (MainWindow) opens the unified Import wizard — the panel can't reach it directly
    /// without a circular reference.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Raised by a collection's "Export…" menu item (tree right-click or the header
    /// ⋯ menu) with that collection. The host (MainWindow) opens the Export dialog with it
    /// pre-selected.</summary>
    public event EventHandler<CollectionRootViewModel>? ExportRequested;

    /// <summary>Raised when the user picks "Open collection" and chooses a folder. The host
    /// (MainWindow) routes this to <c>WorkspacesViewModel.LinkCollection</c> so the path
    /// is persisted in the active workspace and reloads on next launch. The panel can't
    /// see the workspaces VM directly.</summary>
    public event EventHandler<string>? OpenCollectionRequested;

    /// <summary>Default location seeded into the Create-collection dialog. The host updates
    /// this when the active workspace changes (typically <c>&lt;workspace&gt;/collections/</c>).</summary>
    public string DefaultCreateCollectionLocation { get; set; } = "";

    /// <summary>Custom DragDrop format — we don't try to interop with the OS clipboard
    /// (no cross-app drag), just within the panel. The payload is only a marker; the dragged
    /// node itself lives in <see cref="_draggedNode"/> since DataTransfer can't carry objects.</summary>
    private static readonly DataFormat<string> DragFormat =
        DataFormat.CreateStringApplicationFormat("vegha.collection-node");

    /// <summary>Pixel travel before a press becomes a drag — matches Avalonia's default click slop.</summary>
    private const double DragThreshold = 5.0;

    private Point _pressPoint;
    private CollectionNodeViewModel? _pressedNode;
    private bool _dragInProgress;
    private CollectionNodeViewModel? _draggedNode;

    static CollectionsPanel()
    {
        // Pressing a row selects it, and TreeView then brings the whole TreeViewItem into view —
        // for an expanded folder that rect spans every child row. When the folder is taller than
        // the viewport the ScrollViewer jumps mid-click, the release lands on a different row, no
        // Tapped fires, and the folder doesn't collapse. Narrow the request to the header row.
        // A class handler is needed because the ScrollViewer sits between the items and this panel.
        Control.RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>(OnTreeItemRequestBringIntoView);
    }

    private static void OnTreeItemRequestBringIntoView(TreeViewItem tvi, RequestBringIntoViewEventArgs e)
    {
        if (!ReferenceEquals(e.TargetObject, tvi)) return;
        if (tvi.FindAncestorOfType<CollectionsPanel>() is null) return;
        var header = tvi.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_LayoutRoot");
        if (header is null || header.Bounds.Height <= 0) return;
        e.TargetRect = new Rect(0, 0, tvi.Bounds.Width, header.Bounds.Height);
    }

    public CollectionsPanel()
    {
        InitializeComponent();

        // The active-collection menu is shared by the "⋯" button and a right-click on the
        // collection name row: drop down under the button, but open at the pointer for the
        // right-click like every other context menu.
        if (Resources["ActiveCollectionMenu"] is MenuFlyout collectionMenu)
        {
            collectionMenu.Opening += (_, _) =>
                collectionMenu.Placement = ReferenceEquals(collectionMenu.Target, CollectionHeaderRow)
                    ? PlacementMode.Pointer
                    : PlacementMode.BottomEdgeAlignedRight;
        }

        AddHandler(InputElement.DoubleTappedEvent, OnTreeItemDoubleTapped);
        // Single-tap toggle for non-leaf rows. Tapped is a synthesized click event that survives
        // TreeViewItem's selection routing; handledEventsToo guarantees we see it even after.
        AddHandler(InputElement.TappedEvent, OnTreeItemTapped,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag-and-drop: requests + folders can be moved between folders within the panel.
        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Bubble);
        AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ============================== Header context menu ==============================
    // The picker button's right-click + the "..." button both surface the same set of
    // collection-level actions. All handlers target the currently-active collection — the
    // header has no other concept of "which collection" to act on.

    private void OnHeaderNewRequest_Click(object? sender, RoutedEventArgs e) =>
        InvokeActive(vm => vm.CreateRequestCommand);
    private async void OnHeaderNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm || vm.ActiveCollection is null) return;
        await PromptAndCreateFolderAsync(vm, vm.ActiveCollection);
    }

    /// <summary>Tree-side "New Folder" — used by both the collection-root and folder context
    /// menus. The bound MenuItem's DataContext is whatever node was right-clicked.</summary>
    private async void OnTreeNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (sender is not MenuItem mi || mi.DataContext is not CollectionNodeViewModel node) return;
        await PromptAndCreateFolderAsync(vm, node);
    }

    /// <summary>Pops the name prompt; on confirm, asks the VM to create a folder + write a
    /// <c>folder.bru</c> marker. The marker keeps the freshly-created (empty) folder visible
    /// after the watcher reload — without it, CollectionLoader's "skip empty dir" guard
    /// silently drops the new folder.</summary>
    private async Task PromptAndCreateFolderAsync(CollectionsViewModel vm, CollectionNodeViewModel target)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new CreateFolderDialog();
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrWhiteSpace(dlg.Result)) return;
        vm.CreateNamedFolder(target, dlg.Result);
    }
    private void OnHeaderRun_Click(object? sender, RoutedEventArgs e) =>
        InvokeActive(vm => vm.RunCollectionCommand);
    private void OnHeaderClone_Click(object? sender, RoutedEventArgs e) =>
        InvokeActive(vm => vm.CloneNodeCommand);
    private async void OnHeaderRename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm || vm.ActiveCollection is null) return;
        await PromptAndRenameAsync(vm, vm.ActiveCollection);
    }

    private async void OnTreeRename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (sender is not MenuItem mi || mi.Tag is not CollectionNodeViewModel node) return;
        await PromptAndRenameAsync(vm, node);
    }

    /// <summary>Shared rename flow lives in <see cref="CollectionDialogActions"/> so the
    /// top-bar picker and Manage Collections dialog run the same code. This wrapper just
    /// resolves the owning window before delegating.</summary>
    private async Task PromptAndRenameAsync(CollectionsViewModel vm, CollectionNodeViewModel node)
    {
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;
        await CollectionDialogActions.PromptAndRenameAsync(owner, vm, node);
    }

    /// <summary>F2 on a selected tree row opens the same rename flow as the row's context
    /// menu (requests, folders, and collection roots all share it). The synthetic
    /// "+ New request" placeholder has no file to rename, so it's skipped.</summary>
    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        if (DataContext is not CollectionsViewModel vm) return;
        if (CollectionsTree.SelectedItem is not CollectionNodeViewModel node) return;
        if (node is NewRequestPlaceholderViewModel) return;
        e.Handled = true;
        await PromptAndRenameAsync(vm, node);
    }
    private void OnHeaderReveal_Click(object? sender, RoutedEventArgs e) =>
        InvokeActive(vm => vm.RevealInFileExplorerCommand);
    private void OnHeaderSettings_Click(object? sender, RoutedEventArgs e) =>
        InvokeActive(vm => vm.OpenCollectionSettingsCommand);

    private void InvokeActive(Func<CollectionsViewModel, System.Windows.Input.ICommand> selector)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (vm.ActiveCollection is null) return;
        var cmd = selector(vm);
        if (cmd.CanExecute(vm.ActiveCollection)) cmd.Execute(vm.ActiveCollection);
    }

    private async void OnHeaderRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (vm.ActiveCollection is null) return;
        await ConfirmAndRemoveAsync(vm, vm.ActiveCollection);
    }

    private async void OnTreeRemoveCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        // The tree-row MenuItem's DataContext is the bound node (a CollectionRootViewModel).
        if (sender is not MenuItem mi || mi.DataContext is not CollectionRootViewModel root) return;
        await ConfirmAndRemoveAsync(vm, root);
    }

    /// <summary>Shared remove flow lives in <see cref="CollectionDialogActions"/>. This wrapper
    /// resolves the owning window before delegating.</summary>
    private async Task ConfirmAndRemoveAsync(CollectionsViewModel vm, CollectionRootViewModel root)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        await CollectionDialogActions.ConfirmAndRemoveAsync(owner, vm, root);
    }

    private void OnClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionsViewModel vm) vm.Filter = string.Empty;
    }

    private async void OpenCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a Bruno collection folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        // Route through the host so WorkspacesViewModel persists the link — without that,
        // the collection only lives in memory and disappears on next launch.
        if (OpenCollectionRequested is { } handler)
            handler(this, path);
        else
            vm.LoadFromDirectory(path);
    }

    private async void CreateCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dlg = new CreateCollectionDialog(DefaultCreateCollectionLocation);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || dlg.Result is null) return;

        var folder = dlg.Result.FolderPath;
        var name = dlg.Result.Name;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            // Minimal collection.bru so CollectionLoader recognizes the folder as a Bruno
            // collection root and surfaces the user-supplied name.
            var bru = $"meta {{\n  name: {name}\n  type: collection\n}}\n";
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "collection.bru"), bru);
            vm.LoadFromDirectory(folder);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Failed to create collection: {ex.Message}";
        }
    }

    private void ImportCollection_Click(object? sender, RoutedEventArgs e) =>
        ImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnTreeExport_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CollectionRootViewModel root }) ExportRequested?.Invoke(this, root);
    }

    private void OnHeaderExport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionsViewModel { ActiveCollection: { } root }) ExportRequested?.Invoke(this, root);
    }

    private void OnTreeItemDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (e.Source is not Control c) return;
        if (c.DataContext is CollectionItemViewModel item)
        {
            vm.OpenRequestCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>Single-tap on a folder (or collection) row. The chevron only toggles expansion;
    /// a click anywhere else on the row is "open + toggle":
    /// <list type="bullet">
    ///   <item>collapsed row → expand it and open (or activate) its Settings tab;</item>
    ///   <item>expanded row whose Settings tab is already active → collapse it;</item>
    ///   <item>expanded row whose tab isn't active → just bring the Settings tab forward.</item>
    /// </list>
    /// The chevron is a plain Path inside a named Panel (no ToggleButton), so every click on a
    /// folder row reaches us and is handled exactly once.</summary>
    private void OnTreeItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v) return;

        // The "..." row-menu button has its own Click that opens the flyout; don't interpret
        // those taps as folder-toggle clicks.
        if (v.FindAncestorOfType<Button>(includeSelf: true) is Button b
            && b.Classes.Contains("rowMenu")) return;

        var tvi = v.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is not CollectionNodeViewModel node) return;

        // The synthetic "+ New request" row inside an empty folder runs New Request scoped to
        // its parent — the same flow as the folder's context-menu "New Request" entry.
        if (node is NewRequestPlaceholderViewModel placeholder)
        {
            if (DataContext is CollectionsViewModel vm
                && vm.CreateRequestCommand.CanExecute(placeholder.Parent))
                vm.CreateRequestCommand.Execute(placeholder.Parent);
            e.Handled = true;
            return;
        }

        if (node.IsLeaf) return;
        e.Handled = true;

        if (IsOnChevron(v, tvi) || DataContext is not CollectionsViewModel collections)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }
        ClickNode(collections, node, node.IsExpanded, x => node.IsExpanded = x);
    }

    /// <summary>"Open + toggle" rule for a folder row: a collapsed row expands and opens its
    /// Settings tab; an expanded row collapses only when that tab is already active.</summary>
    private static void ClickNode(CollectionsViewModel vm, CollectionNodeViewModel node,
        bool isExpanded, Action<bool> setExpanded)
    {
        if (!isExpanded)
        {
            setExpanded(true);
            OpenSettings(vm, node);
        }
        else if (vm.IsSettingsTabActive?.Invoke(node) == true)
        {
            setExpanded(false);
        }
        else
        {
            OpenSettings(vm, node);
        }
    }

    private static void OpenSettings(CollectionsViewModel vm, CollectionNodeViewModel node)
    {
        switch (node)
        {
            case CollectionFolderViewModel f when vm.OpenFolderSettingsCommand.CanExecute(f):
                vm.OpenFolderSettingsCommand.Execute(f);
                break;
            case CollectionRootViewModel r when vm.OpenCollectionSettingsCommand.CanExecute(r):
                vm.OpenCollectionSettingsCommand.Execute(r);
                break;
        }
    }

    /// <summary>True when the tap landed on the row's expand/collapse chevron (the
    /// <c>PART_ExpandCollapseChevron</c> part of the compact TreeViewItem template).</summary>
    private static bool IsOnChevron(Visual source, TreeViewItem tvi)
    {
        for (Visual? cur = source; cur is not null && !ReferenceEquals(cur, tvi); cur = cur.GetVisualParent())
            if (cur is Control { Name: "PART_ExpandCollapseChevron" }) return true;
        return false;
    }

    /// <summary>Collection header above the tree: a click opens the collection's Settings tab.</summary>
    private void OnCollectionHeaderRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm || vm.ActiveCollection is not { } root) return;
        e.Handled = true;
        OpenSettings(vm, root);
    }

    /// <summary>The hover "..." button on each tree row opens the same flyout the row's
    /// right-click would. The row Grid carries the menu via <c>ContextFlyout</c> (which Avalonia
    /// auto-shows on right-click); for the explicit click we walk up to that Grid and call
    /// <c>ShowAt</c> against it so the popup anchors near the button.</summary>
    private void OnRowMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var row = button.FindAncestorOfType<Grid>();
        if (row?.ContextFlyout is not { } flyout) return;
        flyout.ShowAt(row);
        e.Handled = true;
    }

    // ============================== Drag-and-drop ==============================
    // Holding-press-and-moving on a tree row promotes to a DragDrop. The drop target is
    // the row under the pointer; CollectionsViewModel.MoveNode does the disk-side work
    // (File.Move / Directory.Move) and reloads the affected root.

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Skip the row-menu button — it owns its own clicks.
        if (e.Source is Visual v
            && v.FindAncestorOfType<Button>(includeSelf: true) is Button b
            && b.Classes.Contains("rowMenu"))
        {
            _pressedNode = null;
            return;
        }

        _pressPoint = e.GetPosition(this);
        _pressedNode = ResolveNodeFromSource(e.Source);
        _dragInProgress = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _pressedNode is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) < DragThreshold &&
            Math.Abs(p.Y - _pressPoint.Y) < DragThreshold) return;

        // Roots + the synthetic "+ New request" row aren't draggable — only requests + folders move.
        if (_pressedNode is CollectionRootViewModel or NewRequestPlaceholderViewModel)
        { _pressedNode = null; return; }

        _dragInProgress = true;
        _draggedNode = _pressedNode;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragFormat, "node"));
        _ = RunDragAsync(e, data);
    }

    private async Task RunDragAsync(PointerEventArgs e, DataTransfer data)
    {
        try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move); }
        finally { _draggedNode = null; }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedNode = null;
        _dragInProgress = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_draggedNode is null || !e.DataTransfer.Contains(DragFormat)) { e.DragEffects = DragDropEffects.None; return; }
        var target = ResolveNodeFromSource(e.Source);
        // Dropping onto the "+ New request" row means "into the folder that owns it".
        if (target is NewRequestPlaceholderViewModel ph) target = ph.Parent;
        // Only folders / roots are valid drop targets. Leaf-on-leaf is a no-op.
        if (target is null || target is CollectionItemViewModel)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (!e.DataTransfer.Contains(DragFormat) || _draggedNode is not { } source) return;
        var target = ResolveNodeFromSource(e.Source);
        if (target is NewRequestPlaceholderViewModel ph) target = ph.Parent;
        if (target is null) return;

        if (vm.MoveNode(source, target))
        {
            e.Handled = true;
        }
    }

    private static CollectionNodeViewModel? ResolveNodeFromSource(object? source)
    {
        if (source is not Visual v) return null;
        var tvi = v.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        return tvi?.DataContext as CollectionNodeViewModel;
    }
}
