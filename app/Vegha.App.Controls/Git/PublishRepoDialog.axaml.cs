using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Git;

namespace Vegha.App.Controls.Git;

public partial class PublishRepoDialog : Window
{
    public PublishRepoDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public PublishRepoDialog(PublishRepoViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishRepoViewModel { IsWorking: true } vm) vm.CancelCommand.Execute(null);
        Close(DataContext is PublishRepoViewModel { IsDone: true });
    }

    private void OnCreateMode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishRepoViewModel vm) vm.UseExistingUrl = false;
    }

    private void OnOpenSso_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishRepoViewModel { SsoAuthorizationUrl: { } url }) GitUiHelpers.OpenUrl(url);
    }
}
