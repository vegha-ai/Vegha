using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class WelcomeDialog : Window
{
    public WelcomeDialog()
    {
        InitializeComponent();
    }

    private void OnOpen_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnImport_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnTrySample_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnSkip_Click(object? sender, RoutedEventArgs e) => Close(false);
}
