using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels.Git;

namespace Vegha.App.Controls.Git;

public partial class GitCredentialEditorView : UserControl
{
    public GitCredentialEditorView()
    {
        InitializeComponent();
    }

    private async void OnBrowseKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitCredentialEditorViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var sshDir = await top.StorageProvider.TryGetFolderFromPathAsync(
            System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".ssh"));
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose SSH private key",
            AllowMultiple = false,
            SuggestedStartLocation = sshDir,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            vm.SshKeyPath = path.EndsWith(".pub", System.StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }
}
