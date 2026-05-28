using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Vegha.App.ViewModels;

#nullable enable

namespace Vegha.App.Controls.Workspace;

/// <summary>Shared rename / remove / reveal / settings flows for collection rows. Exists so
/// the sidebar header ("..." menu), the top-bar collection picker, and the Manage Collections
/// dialog all run the same code path — duplicating these handlers per call-site has bitten us
/// before (the rename helper has to write the new name onto the node *before* invoking the
/// command, and that subtlety drifts when copy-pasted).</summary>
public static class CollectionDialogActions
{
    /// <summary>Pops a rename dialog seeded with the node's current name. On confirm, writes the
    /// new name onto the node and invokes <c>RenameNodeCommand</c> — the command no-ops if the
    /// in-memory name hasn't changed, so we must mutate it first.</summary>
    public static async Task PromptAndRenameAsync(
        Window owner, CollectionsViewModel vm, CollectionNodeViewModel node)
    {
        var label = node switch
        {
            CollectionRootViewModel => "Collection name",
            CollectionFolderViewModel => "Folder name",
            _ => "Name",
        };
        var dlg = new RenameDialog("Rename", label, node.Name);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrWhiteSpace(dlg.ResultName)) return;
        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, node.Name, StringComparison.Ordinal)) return;
        node.Name = newName;
        if (vm.RenameNodeCommand.CanExecute(node))
            vm.RenameNodeCommand.Execute(node);
    }

    /// <summary>Pops the destructive warning dialog (showing the on-disk path) and, on confirm,
    /// detaches the collection from the workspace. Files on disk are untouched.</summary>
    public static async Task ConfirmAndRemoveAsync(
        Window owner, CollectionsViewModel vm, CollectionRootViewModel root)
    {
        var dlg = new RemoveCollectionDialog(root.Name, root.SourcePath);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;
        if (vm.RemoveCollectionCommand.CanExecute(root))
            vm.RemoveCollectionCommand.Execute(root);
    }

    /// <summary>Reveals the collection root in the OS file explorer via the VM's command, so
    /// the dropdown / dialog reuse the same path-resolution + error handling as the sidebar.</summary>
    public static void Reveal(CollectionsViewModel vm, CollectionRootViewModel root)
    {
        if (vm.RevealInFileExplorerCommand.CanExecute(root))
            vm.RevealInFileExplorerCommand.Execute(root);
    }

    /// <summary>Invokes the collection-settings command for the given root, matching the
    /// "Settings" entry in the sidebar's "..." menu.</summary>
    public static void OpenSettings(CollectionsViewModel vm, CollectionRootViewModel root)
    {
        if (vm.OpenCollectionSettingsCommand.CanExecute(root))
            vm.OpenCollectionSettingsCommand.Execute(root);
    }
}
