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

    /// <summary>Raised when the user picks "Open collection" and chooses a folder. The host
    /// (MainWindow) routes this to <c>WorkspacesViewModel.LinkCollection</c> so the path
    /// is persisted in the active workspace and reloads on next launch. The panel can't
    /// see the workspaces VM directly.</summary>
    public event EventHandler<string>? OpenCollectionRequested;

    /// <summary>Default location seeded into the Create-collection dialog. The host updates
    /// this when the active workspace changes (typically <c>&lt;workspace&gt;/collections/</c>).</summary>
    public string DefaultCreateCollectionLocation { get; set; } = "";

    /// <summary>Custom DragDrop format key — we don't try to interop with the OS clipboard
    /// (no cross-app drag), just within the panel.</summary>
    private const string DragFormat = "vegha/collection-node";

    /// <summary>Pixel travel before a press becomes a drag — matches Avalonia's default click slop.</summary>
    private const double DragThreshold = 5.0;

    private Point _pressPoint;
    private CollectionNodeViewModel? _pressedNode;
    private bool _dragInProgress;

    public CollectionsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    private CollectionsViewModel? _hookedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hookedVm is not null)
            _hookedVm.NodePropertiesRequested -= OnNodePropertiesRequested;
        if (DataContext is CollectionsViewModel vm)
        {
            _hookedVm = vm;
            vm.NodePropertiesRequested += OnNodePropertiesRequested;
        }
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

    /// <summary>Shared rename flow: pop a name-input dialog, write the new name onto the
    /// node so <c>RenameNodeAsync</c> picks it up (the command persists the rename and
    /// reloads the parent root). Without this the command silently no-ops because the
    /// node's name hasn't changed.</summary>
    private async Task PromptAndRenameAsync(CollectionsViewModel vm, CollectionNodeViewModel node)
    {
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;
        var label = node switch
        {
            CollectionRootViewModel => "Collection name",
            CollectionFolderViewModel => "Folder name",
            _ => "Name",
        };
        var dlg = new Vegha.App.Controls.Workspace.RenameDialog("Rename", label, node.Name);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrWhiteSpace(dlg.ResultName)) return;
        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, node.Name, StringComparison.Ordinal)) return;
        node.Name = newName;
        if (vm.RenameNodeCommand.CanExecute(node))
            vm.RenameNodeCommand.Execute(node);
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

    /// <summary>Pops the warning dialog; on confirm, runs the destructive RemoveCollection
    /// command. The dialog surfaces the on-disk path so users can verify what's being
    /// detached — files on disk are not touched, only the in-app reference is dropped.</summary>
    private async Task ConfirmAndRemoveAsync(CollectionsViewModel vm, CollectionRootViewModel root)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new RemoveCollectionDialog(root.Name, root.SourcePath);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;
        if (vm.RemoveCollectionCommand.CanExecute(root))
            vm.RemoveCollectionCommand.Execute(root);
    }

    private async void OnNodePropertiesRequested(object? sender, NodePropertiesRequest req)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;

        NodePropertiesViewModel propsVm;
        if (req.Root?.Collection is { } collection)
            propsVm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, collection);
        else if (req.Folder?.Folder is { } folder)
            propsVm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, folder);
        else return;

        var dlg = new NodePropertiesDialog { DataContext = propsVm };
        var result = owner is null ? null : await dlg.ShowDialog<bool?>(owner);
        if (result == true)
        {
            var snapshot = propsVm.BuildSnapshot();
            vm.ApplyNodeSnapshot(req, snapshot);
        }
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

    /// <summary>Single-tap on any non-leaf row toggles its expansion. The chevron is a plain
    /// Path (no ToggleButton), so every click on a folder row — chevron, name, or surrounding
    /// area — reaches us and flips <c>IsExpanded</c> exactly once.</summary>
    private void OnTreeItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v) return;

        // The "..." row-menu button has its own Click that opens the flyout; don't interpret
        // those taps as folder-toggle clicks.
        if (v.FindAncestorOfType<Button>(includeSelf: true) is Button b
            && b.Classes.Contains("rowMenu")) return;

        var tvi = v.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is not CollectionNodeViewModel node || node.IsLeaf) return;

        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
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

        // Roots aren't draggable — only requests + folders can be moved.
        if (_pressedNode is CollectionRootViewModel) { _pressedNode = null; return; }

        _dragInProgress = true;
        var data = new DataObject();
        data.Set(DragFormat, _pressedNode);
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedNode = null;
        _dragInProgress = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DragFormat)) { e.DragEffects = DragDropEffects.None; return; }
        var target = ResolveNodeFromSource(e.Source);
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
        if (e.Data.Get(DragFormat) is not CollectionNodeViewModel source) return;
        var target = ResolveNodeFromSource(e.Source);
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
