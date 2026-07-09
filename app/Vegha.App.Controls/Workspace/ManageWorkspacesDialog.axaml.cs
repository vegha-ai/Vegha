using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Manage Workspaces" dialog. Lists every registered workspace and
/// lets the user activate, create, open, or remove them. Default workspace cannot
/// be removed (the Remove button is disabled via <c>{Binding !IsDefault}</c>).</summary>
public partial class ManageWorkspacesDialog : Window
{
    private readonly WorkspacesViewModel? _vm;

    /// <summary>Parameterless ctor for the Avalonia XAML loader / designer. Code paths
    /// that actually open the dialog use the constructor that takes the VM.</summary>
    public ManageWorkspacesDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public ManageWorkspacesDialog(WorkspacesViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
        WorkspaceList.ItemsSource = _vm.Workspaces;
    }

    private async void OnCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new CreateWorkspaceDialog();
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is null) return;
        _vm.CreateWorkspace(dlg.Result.Name, dlg.Result.FolderPath);
    }

    private async void OnOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open existing workspace folder (must contain workspace.yml or be a Bruno collection)",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) _vm.AddWorkspace(path);
    }

    private void OnActivate_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn && btn.Tag is WorkspaceItemViewModel item)
            _vm.ActiveWorkspace = item;
    }

    private void OnRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn && btn.Tag is WorkspaceItemViewModel item)
            _vm.RemoveWorkspaceCommand.Execute(item);
    }

    /// <summary>Renames the row's workspace in place — no activation side effect (the old
    /// "Edit" action activated the workspace before opening the retired workspace editor,
    /// which surprised users who only wanted to change a name).</summary>
    private async void OnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button btn || btn.Tag is not WorkspaceItemViewModel item) return;
        var dlg = new RenameDialog("Rename workspace", "Workspace name", item.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrWhiteSpace(dlg.ResultName)) return;
        _vm.RenameWorkspace(item, dlg.ResultName);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
