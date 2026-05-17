using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class AppStatusBar : UserControl
{
    public AppStatusBar()
    {
        InitializeComponent();
    }

    private void OnBranchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Make sure the branch list is current — git can change under us between clicks.
        _ = vm.Git.RefreshAsync();
        BranchPopup.IsOpen = !BranchPopup.IsOpen;
    }

    private void OnBranchItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button b || b.Tag is not string branch) return;
        BranchPopup.IsOpen = false;
        if (!string.Equals(branch, vm.Git.CurrentBranch, System.StringComparison.Ordinal))
            _ = vm.Git.CheckoutAsync(branch);
    }

    /// <summary>Clicking a remote branch (e.g. "origin/feature") creates a local tracking
    /// branch with the bare name ("feature") and checks it out — matches VSCode's behavior.</summary>
    private void OnRemoteBranchItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button b || b.Tag is not string remoteRef) return;
        BranchPopup.IsOpen = false;

        // origin/foo → foo. Strip the first segment so the local branch matches the remote's bare name.
        var slash = remoteRef.IndexOf('/');
        var localName = slash >= 0 ? remoteRef[(slash + 1)..] : remoteRef;

        // If the local branch already exists, just check out; otherwise create+checkout.
        if (vm.Git.Branches.Contains(localName))
            _ = vm.Git.CheckoutAsync(localName);
        else
            _ = vm.Git.CreateBranchAsync(localName);
    }

    private async void OnCreateBranch_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        BranchPopup.IsOpen = false;

        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;

        var dialog = new CreateBranchDialog(vm.Git.CurrentBranch);
        var ok = await dialog.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrEmpty(dialog.ResultName)) return;

        await vm.Git.CreateBranchAsync(dialog.ResultName);
    }

    private async void OnCookiesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;

        var dialog = new CookiesDialog(vm.Cookies);
        await dialog.ShowDialog(owner);
    }
}
