using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class GitPanel : UserControl
{
    /// <summary>Raised when the user clicks a change row (or its "Open changes" button). The
    /// host (MainWindow) routes this to <c>OpenTabsViewModel</c> as a new diff tab.</summary>
    public event EventHandler<GitChangeRow>? OpenDiffRequested;

    /// <summary>Raised when the user picks Configure... on the author-warning banner. The
    /// host shows a small dialog and writes via <c>GitViewModel.SetGitIdentityCommand</c>.</summary>
    public event EventHandler? ConfigureIdentityRequested;

    public GitPanel()
    {
        InitializeComponent();
    }

    private void OnStage_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        vm.StageCommand.Execute(row);
    }

    private void OnUnstage_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        vm.UnstageCommand.Execute(row);
    }

    private void OnDiscardFile_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        vm.DiscardFileCommand.Execute(row);
    }

    private async void OnDiscardAll_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is not null)
        {
            var count = vm.StagedChanges.Count + vm.UnstagedChanges.Count + vm.UntrackedChanges.Count;
            var dlg = new Vegha.App.Controls.Workspace.ConfirmDiscardDialog(
                $"Discard changes in {count} file{(count == 1 ? "" : "s")}? This is destructive.");
            var ok = await dlg.ShowDialog<bool>(owner);
            if (!ok) return;
        }
        vm.DiscardAllCommand.Execute(null);
    }

    private void OnOpenDiff_Click(object? s, RoutedEventArgs e)
    {
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        OpenDiffRequested?.Invoke(this, row);
        if (DataContext is GitViewModel vm) vm.OpenDiffCommand.Execute(row);
    }

    private void OnRow_PointerPressed(object? s, PointerPressedEventArgs e)
    {
        // Single-click on the row body opens the diff (matching VSCode behavior). Hover
        // action clicks bubble up here too via routed events; ignore those by checking
        // whether the originating control is a Button.
        if (e.Source is Control src && IsInsideButton(src)) return;
        if (s is not Control c || c.Tag is not GitChangeRow row) return;
        OpenDiffRequested?.Invoke(this, row);
        if (DataContext is GitViewModel vm)
        {
            vm.SelectedChange = row;
            vm.OpenDiffCommand.Execute(row);
        }
    }

    private static bool IsInsideButton(Control c)
    {
        Control? cur = c;
        while (cur is not null)
        {
            if (cur is Button) return true;
            cur = cur.Parent as Control;
        }
        return false;
    }

    private void OnResolveOurs_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        vm.ResolveOursCommand.Execute(row);
    }

    private void OnResolveTheirs_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control b || b.Tag is not GitChangeRow row) return;
        vm.ResolveTheirsCommand.Execute(row);
    }

    private void OnAddToGitignore_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control c || c.Tag is not GitChangeRow row) return;
        vm.AddToGitignoreCommand.Execute(row);
    }

    private void OnOpenFile_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control c || c.Tag is not GitChangeRow row) return;
        vm.OpenFileCommand.Execute(row);
    }

    private void OnConfigureIdentity_Click(object? s, RoutedEventArgs e) =>
        ConfigureIdentityRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the stash-name dialog and routes the user's input (an optional name)
    /// into <c>GitViewModel.StashCommand</c>. Cancel skips the stash entirely.</summary>
    private async void OnStash_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (!vm.IsRepoActive) return;
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null)
        {
            // No owner window (e.g., headless tests) — fall back to an unnamed stash.
            vm.StashCommand.Execute(null);
            return;
        }
        var dlg = new Vegha.App.Controls.Workspace.StashDialog();
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;
        vm.StashCommand.Execute(dlg.ResultName ?? string.Empty);
    }

    private void OnStashApply_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control c || c.Tag is not GitStashRow row) return;
        vm.StashApplyCommand.Execute(row.Index);
    }

    /// <summary>"Pop" semantics: apply the chosen stash then remove it. libgit2sharp's
    /// <c>Pop</c> only takes the top of the stack, so for non-top entries we apply-then-drop
    /// to match the user's "pop X" expectation.</summary>
    private void OnStashPop_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control c || c.Tag is not GitStashRow row) return;
        if (row.Index == 0) vm.StashPopCommand.Execute(null);
        else
        {
            vm.StashApplyCommand.Execute(row.Index);
            vm.StashDropCommand.Execute(row.Index);
        }
    }

    private void OnStashDrop_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        if (s is not Control c || c.Tag is not GitStashRow row) return;
        vm.StashDropCommand.Execute(row.Index);
    }

    private async void OnManageRemotes_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not GitViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;
        var dlg = new Vegha.App.Controls.Workspace.ManageRemotesDialog(vm);
        await dlg.ShowDialog(owner);
        _ = vm.RefreshAsync();
    }
}
