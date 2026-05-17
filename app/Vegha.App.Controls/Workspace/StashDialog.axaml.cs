using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

/// <summary>Small modal that collects an optional stash name. Returns the trimmed name
/// (or empty string for an unnamed stash) on OK; returns null on Cancel.</summary>
public partial class StashDialog : Window
{
    public string? ResultName { get; private set; }

    public StashDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        ResultName = NameBox.Text?.Trim() ?? string.Empty;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        ResultName = null;
        Close(false);
    }
}
