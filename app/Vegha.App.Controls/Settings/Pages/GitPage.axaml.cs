using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.Controls.Git;
using Vegha.App.ViewModels.Settings;

namespace Vegha.App.Controls.Settings.Pages;

public partial class GitPage : UserControl
{
    public GitPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not GitSettingsViewModel vm) return;
            vm.CopyToClipboard = text => GitUiHelpers.CopyAsync(this, text);
            vm.OpenUrl = GitUiHelpers.OpenUrl;
        };
    }

    private async void OnBrowseGit_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitSettingsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the git executable",
            AllowMultiple = false,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.GitExecutablePath = path;
    }
}
