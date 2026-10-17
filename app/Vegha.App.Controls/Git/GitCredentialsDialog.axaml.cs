using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Git;

namespace Vegha.App.Controls.Git;

public partial class GitCredentialsDialog : Window
{
    public GitCredentialsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public GitCredentialsDialog(GitCredentialEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.OpenUrl ??= GitUiHelpers.OpenUrl;
        vm.Deleted += (_, _) => Close(true);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close(true);
}
