using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class ManageRemotesDialog : Window
{
    private readonly GitViewModel? _vm;

    public ManageRemotesDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public ManageRemotesDialog(GitViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private async void OnAdd_Click(object? s, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new AddRemoteDialog();
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrWhiteSpace(dlg.RemoteName) || string.IsNullOrWhiteSpace(dlg.RemoteUrl)) return;
        await _vm.AddRemoteAsync((dlg.RemoteName!, dlg.RemoteUrl!));
    }

    private async void OnEdit_Click(object? s, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (s is not Button b || b.Tag is not GitRemoteRow row) return;
        var dlg = new AddRemoteDialog(row.Name, row.Url, editMode: true);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrWhiteSpace(dlg.RemoteUrl)) return;
        await _vm.UpdateRemoteUrlAsync((row.Name, dlg.RemoteUrl!));
    }

    private async void OnRemove_Click(object? s, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (s is not Button b || b.Tag is not GitRemoteRow row) return;
        var confirm = new ConfirmDiscardDialog($"Remove remote '{row.Name}' ({row.Url})? Existing branches stay; only the remote pointer is deleted.");
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return;
        await _vm.RemoveRemoteAsync(row.Name);
    }

    private void OnClose_Click(object? s, RoutedEventArgs e) => Close();
}
