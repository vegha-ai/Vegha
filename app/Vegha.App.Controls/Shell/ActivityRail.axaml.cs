using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class ActivityRail : UserControl
{
    /// <summary>Raised when the user clicks Settings or Help. The host opens the corresponding
    /// dialog/window — keeping it as an event keeps the rail decoupled from window plumbing.</summary>
    public event EventHandler? SettingsRequested;
    public event EventHandler? HelpRequested;

    public ActivityRail()
    {
        InitializeComponent();
    }

    private void OnCollections_Click(object? sender, RoutedEventArgs e) => SetSection("collections");
    private void OnEnvironments_Click(object? sender, RoutedEventArgs e) => SetSection("environments");
    private void OnHistory_Click(object? sender, RoutedEventArgs e) => SetSection("history");
    private void OnGit_Click(object? sender, RoutedEventArgs e) => SetSection("git");
    private void OnOpenApi_Click(object? sender, RoutedEventArgs e) => SetSection("openapi");
    private void OnRunner_Click(object? sender, RoutedEventArgs e) => SetSection("runner");

    private void OnSettings_Click(object? sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnHelp_Click(object? sender, RoutedEventArgs e) =>
        HelpRequested?.Invoke(this, EventArgs.Empty);

    private void SetSection(string section)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ActiveSidebarSection = section;
    }
}
