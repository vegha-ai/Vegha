using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;

#nullable enable

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Manage Collections" dialog for the active workspace. Lists every collection
/// linked to the workspace and lets the user activate, rename, reveal, or remove them — plus
/// Create / Open / Import entry points in the footer. Mirrors <see cref="ManageWorkspacesDialog"/>
/// shape so the two surfaces feel like siblings.</summary>
public partial class ManageCollectionsDialog : Window
{
    private readonly CollectionsViewModel? _collections;
    private readonly WorkspacesViewModel? _workspaces;

    /// <summary>Raised when the user clicks "Import…". The host (AppTopBar) opens the Import
    /// wizard — the dialog can't reach the wizard factory itself without a circular reference.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Parameterless ctor for the Avalonia XAML loader / designer.</summary>
    public ManageCollectionsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public ManageCollectionsDialog(CollectionsViewModel collections, WorkspacesViewModel workspaces)
    {
        _collections = collections;
        _workspaces = workspaces;
        InitializeComponent();
        Opened += (_, _) =>
        {
            this.RemoveMinimizeMaximize();
            FilterBox?.Focus();
        };
        SubtitleText.Text = workspaces.ActiveWorkspace is { } ws
            ? $"Workspace: {ws.Name}"
            : "No active workspace";
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        // Reflect external mutations (LinkCollection, RemoveCollection, async watcher reloads)
        // back into the filtered snapshot. Without this, a rename that comes back from disk
        // doesn't update the dialog row until the user re-types in the filter.
        _collections.AvailableCollections.CollectionChanged += OnAvailableCollectionsChanged;
        Closed += (_, _) =>
            _collections.AvailableCollections.CollectionChanged -= OnAvailableCollectionsChanged;
        ApplyFilter();
    }

    private void OnAvailableCollectionsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ApplyFilter();

    /// <summary>Rebuilds <c>CollectionList.ItemsSource</c> from the filtered set. Keeps the
    /// underlying observable collection mutation-free — we hand the ListBox a filtered snapshot
    /// each time so renames/removes outside the dialog don't fight our filter.</summary>
    private void ApplyFilter()
    {
        if (_collections is null) return;
        var filter = FilterBox?.Text ?? string.Empty;
        ClearFilterButton.IsVisible = !string.IsNullOrEmpty(filter);

        var matches = _collections.AvailableCollections
            .Where(c => string.IsNullOrEmpty(filter) ||
                        (c.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        CollectionList.ItemsSource = matches;
        EmptyState.IsVisible = matches.Count == 0;
    }

    private void OnClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (FilterBox is not null) FilterBox.Text = string.Empty;
    }

    private void OnActivate_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is Button btn && btn.Tag is CollectionRootViewModel root)
            _collections.ActiveCollection = root;
    }

    private async void OnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is not Button btn || btn.Tag is not CollectionRootViewModel root) return;
        await CollectionDialogActions.PromptAndRenameAsync(this, _collections, root);
        // Rename may have re-keyed the row; refresh the filtered view so the new name shows
        // immediately even when there's an active filter.
        ApplyFilter();
    }

    private void OnReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is Button btn && btn.Tag is CollectionRootViewModel root)
            CollectionDialogActions.Reveal(_collections, root);
    }

    private async void OnRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is not Button btn || btn.Tag is not CollectionRootViewModel root) return;
        await CollectionDialogActions.ConfirmAndRemoveAsync(this, _collections, root);
        ApplyFilter();
    }

    /// <summary>Delete… — permanently removes the collection folder from disk (unlike
    /// Remove, which only detaches it). Lived in the retired workspace editor before;
    /// this dialog is its new home.</summary>
    private async void OnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is not Button btn || btn.Tag is not CollectionRootViewModel root) return;

        var confirm = new CloseWorkspaceDialog(workspaceName: root.Name, workspacePath: root.SourcePath)
        {
            Title = "Delete collection",
        };
        confirm.SetPromptForCollectionDelete();
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return;

        try
        {
            if (System.IO.Directory.Exists(root.SourcePath))
                System.IO.Directory.Delete(root.SourcePath, recursive: true);
            _collections.RemoveCollectionCommand.Execute(root);
            _collections.StatusMessage = $"Deleted “{root.Name}” from disk.";
        }
        catch (Exception ex)
        {
            _collections.StatusMessage = $"Delete failed: {ex.Message}";
        }
        ApplyFilter();
    }

    /// <summary>Create… — same flow as the top-bar "+ Create collection" path: pop the
    /// create dialog, then LinkCollection so the new path persists in workspaces.json.</summary>
    private async void OnCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspaces is null || _collections is null) return;
        var defaultDir = _workspaces.ActiveWorkspace is { } ws
            ? System.IO.Path.Combine(ws.FolderPath, "collections")
            : string.Empty;
        var dlg = new CreateCollectionDialog(defaultDir);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is null) return;

        var folder = dlg.Result.FolderPath;
        var name = dlg.Result.Name;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var bru = $"meta {{\n  name: {name}\n  type: collection\n}}\n";
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "collection.bru"), bru);
            Vegha.Core.Persistence.WorkspaceBootstrapper.EnsureCollectionGitIgnore(folder);
            _workspaces.LinkCollection(folder);
        }
        catch (Exception ex)
        {
            _collections.StatusMessage = $"Failed to create collection: {ex.Message}";
        }
        ApplyFilter();
    }

    /// <summary>Open… — folder picker → LinkCollection. Same as AppTopBar's
    /// OnOpenCollection_Click.</summary>
    private async void OnOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspaces is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a collection folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        _workspaces.LinkCollection(path);
        ApplyFilter();
    }

    /// <summary>Import… closes the dialog first, then bubbles to AppTopBar's existing
    /// ImportRequested → MainWindow → Import wizard flow. Closing first avoids stacking the
    /// wizard on top of this dialog and keeps focus management sane.</summary>
    private void OnImport_Click(object? sender, RoutedEventArgs e)
    {
        Close();
        ImportRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
