using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class AppStatusBar : UserControl
{
    private CollectionsViewModel? _hookedCollections;
    private DispatcherTimer? _statusToastTimer;

    public AppStatusBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RehookCollections();
        // Belt-and-suspenders: DataContextChanged sometimes doesn't fire when the control
        // inherits its DataContext from the visual-tree parent at attach time. Re-hooking
        // on attach guarantees we have the latest CollectionsViewModel reference even when
        // DataContext was already inherited before our constructor ran.
        AttachedToVisualTree += (_, _) => RehookCollections();
    }

    /// <summary>Hooks the active <see cref="CollectionsViewModel"/> so we can auto-clear
    /// <c>StatusMessage</c> after a few seconds — the existing callers (LoadFromDirectory,
    /// Import, etc.) never explicitly clear it, so without this the toast pill would stay
    /// sticky forever.</summary>
    private void RehookCollections()
    {
        var next = (DataContext as MainWindowViewModel)?.Collections;
        if (ReferenceEquals(next, _hookedCollections)) return;
        if (_hookedCollections is not null)
            _hookedCollections.PropertyChanged -= OnCollectionsPropertyChanged;
        _hookedCollections = next;
        if (_hookedCollections is not null)
            _hookedCollections.PropertyChanged += OnCollectionsPropertyChanged;
    }

    private void OnCollectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CollectionsViewModel.StatusMessage)) return;
        if (_hookedCollections is null || string.IsNullOrEmpty(_hookedCollections.StatusMessage)) return;
        // Restart the timer on every status change so a back-to-back message (e.g. 38
        // LoadFromDirectory pings during a bulk import) keeps the full visibility window.
        _statusToastTimer?.Stop();
        _statusToastTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(10) };
        _statusToastTimer.Tick += (_, _) =>
        {
            _statusToastTimer?.Stop();
            if (_hookedCollections is not null) _hookedCollections.StatusMessage = null;
        };
        _statusToastTimer.Start();
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

    private async void OnVersionButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as global::Avalonia.Controls.Window;
        if (owner is null) return;

        var dialog = new AboutDialog();
        await dialog.ShowDialog(owner);
    }
}
