using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Where a "+" scratch request should be saved: the chosen folder's on-disk directory
/// plus the request name.</summary>
public sealed record SaveToCollectionResult(string DirectoryPath, string Name);

/// <summary>One selectable folder in <see cref="SaveToCollectionDialog"/>. Derives from
/// <see cref="CollectionNodeViewModel"/> so the shared <c>TreeView.compact</c> theme (small
/// chevron, hidden when the folder has no sub-folders) applies unchanged. Only folders are
/// represented — never request leaves, and never the collection root itself.</summary>
public sealed class SaveTargetNode : CollectionNodeViewModel
{
    /// <summary>On-disk directory the request file is written into when this folder is the target.</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    public override bool IsLeaf => Children.Count == 0;
}

/// <summary>Modal picker for saving a scratch request into the CURRENT collection. The tree lists
/// only that collection's folders (the collection name is in the window title, not the tree);
/// leaving the selection empty saves at the collection root. New folders can be created within the
/// collection. On confirm the chosen directory + name are exposed via <see cref="Result"/>.</summary>
public partial class SaveToCollectionDialog : Window
{
    public SaveToCollectionResult? Result { get; private set; }

    private string _collectionName = string.Empty;
    private string _collectionRootDir = string.Empty;
    private readonly ObservableCollection<SaveTargetNode> _rootNodes = new();
    private SaveTargetNode? _selected;

    public SaveToCollectionDialog() { InitializeComponent(); }

    /// <param name="collection">The collection to save into — its folders populate the tree, its
    /// root is the default destination, and its name goes in the title.</param>
    /// <param name="defaultName">Initial request name (the scratch tab's title).</param>
    public SaveToCollectionDialog(CollectionRootViewModel collection, string defaultName)
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();

        _collectionName = collection.Name;
        _collectionRootDir = collection.SourcePath;
        Title = $"Save to collection {collection.Name}";

        NameBox.Text = defaultName;
        NameBox.SelectAll();

        foreach (var child in collection.Children)
            if (child is CollectionFolderViewModel folder) _rootNodes.Add(BuildFolder(folder));
        FolderTree.ItemsSource = _rootNodes;

        // The compact tree's chevron is a plain (non-interactive) Path — wire expansion ourselves:
        // a tap on the chevron column toggles, and double-tapping a folder row toggles too. A plain
        // single tap on the row still selects (the save target) via the TreeView's own handling.
        FolderTree.AddHandler(InputElement.TappedEvent, OnFolderTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        FolderTree.AddHandler(InputElement.DoubleTappedEvent, OnFolderDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        UpdateLocation();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        // Only a tap on the chevron column toggles expansion; row taps fall through to selection.
        if (e.Source is Visual v && IsOnChevron(v)) ToggleExpansion(v, e);
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v) ToggleExpansion(v, e);
    }

    private static void ToggleExpansion(Visual source, RoutedEventArgs e)
    {
        var tvi = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is not SaveTargetNode node || node.IsLeaf) return;
        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }

    private static bool IsOnChevron(Visual? v)
    {
        while (v is not null and not TreeViewItem)
        {
            if (v is Control { Name: "PART_ExpandCollapseChevron" }) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static SaveTargetNode BuildFolder(CollectionFolderViewModel folder)
    {
        var node = new SaveTargetNode { Name = folder.Name, Path = folder.Path, DirectoryPath = folder.Path };
        foreach (var child in folder.Children)
            if (child is CollectionFolderViewModel sub) node.Children.Add(BuildFolder(sub));
        return node;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = FolderTree.SelectedItem as SaveTargetNode;
        UpdateLocation();
    }

    /// <summary>Shows where the request will land — the collection root, or a folder path under it.</summary>
    private void UpdateLocation()
    {
        string where = _collectionName;
        if (_selected is not null)
        {
            var rel = Path.GetRelativePath(_collectionRootDir, _selected.DirectoryPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            where = $"{_collectionName} / {rel}";
        }
        ResetHintColor();
        LocationText.Text = $"Saves in: {where}";
    }

    private async void OnNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new CreateFolderDialog();
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrWhiteSpace(dlg.Result)) return;

        try
        {
            FolderTree.SelectedItem = CreateFolderNode(_selected, dlg.Result.Trim());
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't create folder: {ex.Message}");
        }
    }

    /// <summary>Creates <paramref name="displayName"/> as a subfolder of <paramref name="parent"/>
    /// (or at the collection root when null): makes the on-disk directory + <c>folder.bru</c> marker
    /// and adds — or reuses — the matching tree node, returning it. Split out of the click handler so
    /// it's unit-testable without driving the modal name prompt.
    ///
    /// <para>The add must branch on the target: <c>parent.Children</c> is typed
    /// <c>ObservableCollection&lt;CollectionNodeViewModel&gt;</c> (the base) while <c>_rootNodes</c> is
    /// <c>ObservableCollection&lt;SaveTargetNode&gt;</c>. They're invariant generic types, so coercing
    /// both into one <c>IList&lt;CollectionNodeViewModel&gt;</c> threw an InvalidCastException whenever
    /// nothing was selected (an empty collection, or before a folder was picked).</para></summary>
    internal SaveTargetNode CreateFolderNode(SaveTargetNode? parent, string displayName)
    {
        var parentDir = parent?.DirectoryPath ?? _collectionRootDir;
        var dir = Path.Combine(parentDir, Sanitize(displayName));
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            // folder.bru marker — keeps the loader's empty-folder skip guard happy and
            // preserves the chosen display name even when it had to be sanitized for disk.
            File.WriteAllText(
                Path.Combine(dir, "folder.bru"),
                $"meta {{\n  name: {displayName}\n  type: folder\n  seq: 1\n}}\n");
        }

        var siblings = parent is not null ? parent.Children.OfType<SaveTargetNode>() : _rootNodes;
        var existing = siblings.FirstOrDefault(c =>
            string.Equals(c.DirectoryPath, dir, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var child = new SaveTargetNode { Name = displayName, Path = dir, DirectoryPath = dir };
        if (parent is not null) { parent.Children.Add(child); parent.IsExpanded = true; }
        else _rootNodes.Add(child);
        return child;
    }

    private void OnSave_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { ShowError("Enter a request name."); return; }

        var dir = _selected?.DirectoryPath ?? _collectionRootDir;
        Result = new SaveToCollectionResult(dir, name);
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }

    private void ShowError(string message)
    {
        if (this.TryFindResource("DangerBrush", out var brush) && brush is global::Avalonia.Media.IBrush b)
            LocationText.Foreground = b;
        LocationText.Text = message;
    }

    private void ResetHintColor()
    {
        if (this.TryFindResource("Text2Brush", out var brush) && brush is global::Avalonia.Media.IBrush b)
            LocationText.Foreground = b;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "folder" : s;
    }
}
