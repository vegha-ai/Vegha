using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

#nullable enable

namespace Vegha.App.Controls.Workspace;

/// <summary>Post-import summary dialog. Lists just the collections that landed during the
/// current Import wizard run — separate from <see cref="ManageCollectionsDialog"/> which
/// lists every collection in the workspace. Two per-row actions:
/// <c>Open</c> activates the collection and closes the dialog so the user can start using
/// it; <c>Rename</c> reuses the shared <see cref="CollectionDialogActions"/> helper for
/// consistency with the picker + management dialog.</summary>
public partial class ImportSummaryDialog : Window
{
    private readonly CollectionsViewModel? _collections;

    /// <summary>Parameterless ctor for the Avalonia XAML loader / designer.</summary>
    public ImportSummaryDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public ImportSummaryDialog(
        IReadOnlyList<CollectionRootViewModel> imported,
        CollectionsViewModel collections,
        WorkspacesViewModel workspaces)
    {
        _collections = collections;
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();

        var count = imported.Count;
        HeaderText.Text = count == 1
            ? "1 collection imported"
            : $"{count} collections imported";
        SubtitleText.Text = workspaces.ActiveWorkspace is { } ws
            ? $"Landed in workspace: {ws.Name}"
            : "Review and open the collection you'd like to use.";
        CollectionList.ItemsSource = imported.ToList();
    }

    /// <summary>"Open" activates the picked collection so the sidebar tree + top-bar header
    /// switch to it, then closes the dialog — that's the natural next step after seeing
    /// "<i>N collections imported</i>".</summary>
    private void OnActivate_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is Button btn && btn.Tag is CollectionRootViewModel root)
        {
            _collections.ActiveCollection = root;
            Close();
        }
    }

    private async void OnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        if (sender is not Button btn || btn.Tag is not CollectionRootViewModel root) return;
        await CollectionDialogActions.PromptAndRenameAsync(this, _collections, root);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
