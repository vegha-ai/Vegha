using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.Controls.Workspace;
using Vegha.Integrations.Git;

namespace Vegha.App.Controls.Git;

public partial class GitSecretScanDialog : Window
{
    public GitSecretScanDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public GitSecretScanDialog(IReadOnlyList<GitSecretFinding> findings) : this()
    {
        FindingsList.ItemsSource = findings;
    }

    private void OnCommit_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
