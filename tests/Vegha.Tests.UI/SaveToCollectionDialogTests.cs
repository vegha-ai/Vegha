using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Covers the "Save to collection {name}" folder picker: the tree lists only the active
/// collection's folders (no collection-root node, no request leaves), the collection name is in the
/// window title, an empty selection saves at the collection root, and picking a folder targets it.</summary>
public class SaveToCollectionDialogTests
{
    private static (CollectionRootViewModel Root, string RootPath, string FolderPath) BuildRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "vegha-stc-" + Guid.NewGuid().ToString("N"));
        var folderPath = Path.Combine(rootPath, "Sub");
        var root = new CollectionRootViewModel { Name = "EU API", Path = rootPath, SourcePath = rootPath };
        root.Children.Add(new CollectionFolderViewModel { Name = "Sub", Path = folderPath });
        root.Children.Add(new CollectionItemViewModel { Name = "req", Path = Path.Combine(rootPath, "req.bru") });
        return (root, rootPath, folderPath);
    }

    private static List<SaveTargetNode> TreeNodes(SaveToCollectionDialog dlg)
    {
        var tree = dlg.GetVisualDescendants().OfType<TreeView>().First();
        return ((IEnumerable<object>)tree.ItemsSource!).Cast<SaveTargetNode>().ToList();
    }

    private static void ClickSave(SaveToCollectionDialog dlg)
    {
        var save = dlg.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Save");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public void Title_ContainsCollectionName()
    {
        var (root, _, _) = BuildRoot();
        var dlg = new SaveToCollectionDialog(root, "Untitled");
        dlg.Show();
        try { dlg.Title.Should().Be("Save to collection EU API"); }
        finally { dlg.Close(); }
    }

    [AvaloniaFact]
    public void Tree_ShowsFoldersOnly_NoCollectionNode_NoRequestLeaves()
    {
        var (root, _, _) = BuildRoot();
        var dlg = new SaveToCollectionDialog(root, "Untitled");
        dlg.Show();
        try
        {
            var nodes = TreeNodes(dlg);
            nodes.Should().ContainSingle("only the collection's folders are shown — no collection root node");
            nodes[0].Name.Should().Be("Sub");
            nodes.Select(n => n.Name).Should().NotContain("EU API").And.NotContain("req");
        }
        finally { dlg.Close(); }
    }

    [AvaloniaFact]
    public void EmptySelection_SavesAtCollectionRoot()
    {
        var (root, rootPath, _) = BuildRoot();
        var dlg = new SaveToCollectionDialog(root, "MyReq");
        dlg.Show();
        try
        {
            ClickSave(dlg);
            dlg.Result.Should().NotBeNull();
            dlg.Result!.DirectoryPath.Should().Be(rootPath);
            dlg.Result.Name.Should().Be("MyReq");
        }
        finally { dlg.Close(); }
    }

    [AvaloniaFact]
    public void ExpandingFolder_RealizesSubfolders()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "vegha-stc-" + Guid.NewGuid().ToString("N"));
        var subPath = Path.Combine(rootPath, "Sub");
        var innerPath = Path.Combine(subPath, "Inner");
        var root = new CollectionRootViewModel { Name = "C", Path = rootPath, SourcePath = rootPath };
        var sub = new CollectionFolderViewModel { Name = "Sub", Path = subPath };
        sub.Children.Add(new CollectionFolderViewModel { Name = "Inner", Path = innerPath });
        root.Children.Add(sub);

        var dlg = new SaveToCollectionDialog(root, "x");
        dlg.Show();
        try
        {
            var top = TreeNodes(dlg)[0];
            top.Name.Should().Be("Sub");
            top.Children.Should().ContainSingle("the sub-folder is part of the tree data");
            top.Children[0].Name.Should().Be("Inner");

            // Collapsed → the sub-folder row isn't realized; expanding reveals it.
            var tree = dlg.GetVisualDescendants().OfType<TreeView>().First();
            top.IsExpanded = true;
            dlg.UpdateLayout();
            tree.UpdateLayout();

            var visibleNames = tree.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text).Where(t => t is not null).ToList();
            visibleNames.Should().Contain("Inner", "expanding a folder shows its sub-folders");
        }
        finally { dlg.Close(); }
    }

    [AvaloniaFact]
    public void NewFolder_WithNothingSelected_AddsRootFolder_DoesNotThrow()
    {
        // Regression: when no folder is selected the add path tried to coerce
        // ObservableCollection<SaveTargetNode> into IList<CollectionNodeViewModel>, which threw an
        // InvalidCastException — so "New Folder" failed in an empty collection / before a pick.
        var rootPath = Path.Combine(Path.GetTempPath(), "vegha-stc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var root = new CollectionRootViewModel { Name = "Empty", Path = rootPath, SourcePath = rootPath };
            var dlg = new SaveToCollectionDialog(root, "Untitled");
            dlg.Show();
            try
            {
                var node = dlg.CreateFolderNode(null, "Fresh");

                node.Name.Should().Be("Fresh");
                node.DirectoryPath.Should().Be(Path.Combine(rootPath, "Fresh"));
                Directory.Exists(node.DirectoryPath).Should().BeTrue("the folder is created on disk");
                TreeNodes(dlg).Should().ContainSingle().Which.Name.Should().Be("Fresh");
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(rootPath, recursive: true); } catch { } }
    }

    [AvaloniaFact]
    public void NewFolder_UnderSelectedFolder_NestsAndExpands()
    {
        var (root, _, folderPath) = BuildRoot();
        Directory.CreateDirectory(folderPath);
        try
        {
            var dlg = new SaveToCollectionDialog(root, "Untitled");
            dlg.Show();
            try
            {
                var parent = TreeNodes(dlg)[0]; // the "Sub" folder
                var node = dlg.CreateFolderNode(parent, "Nested");

                node.DirectoryPath.Should().Be(Path.Combine(folderPath, "Nested"));
                parent.Children.OfType<SaveTargetNode>().Should().ContainSingle(c => c.Name == "Nested");
                parent.IsExpanded.Should().BeTrue("the parent expands to reveal the new subfolder");
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(folderPath, recursive: true); } catch { } }
    }

    [AvaloniaFact]
    public void SelectingFolder_SavesIntoThatFolder()
    {
        var (root, _, folderPath) = BuildRoot();
        var dlg = new SaveToCollectionDialog(root, "MyReq");
        dlg.Show();
        try
        {
            var tree = dlg.GetVisualDescendants().OfType<TreeView>().First();
            tree.SelectedItem = TreeNodes(dlg)[0]; // the "Sub" folder

            ClickSave(dlg);
            dlg.Result!.DirectoryPath.Should().Be(folderPath);
        }
        finally { dlg.Close(); }
    }
}
