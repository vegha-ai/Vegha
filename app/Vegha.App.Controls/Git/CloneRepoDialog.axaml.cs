using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Git;

namespace Vegha.App.Controls.Git;

public partial class CloneRepoDialog : Window
{
    public CloneOutcome? Outcome { get; private set; }

    public CloneRepoDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public CloneRepoDialog(CloneRepoViewModel vm) : this()
    {
        DataContext = vm;
        vm.Completed += (_, outcome) =>
        {
            Outcome = outcome;
            Close(true);
        };
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CloneRepoViewModel { IsCloning: true } vm) vm.CancelCommand.Execute(null);
        Close(false);
    }

    private async void OnBrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CloneRepoViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the parent folder for the clone",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.ParentFolder = path;
    }

    private void OnOpenSso_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CloneRepoViewModel { SsoAuthorizationUrl: { } url }) GitUiHelpers.OpenUrl(url);
    }
}
