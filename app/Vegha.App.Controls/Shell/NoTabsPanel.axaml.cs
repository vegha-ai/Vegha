using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Shell;

public partial class NoTabsPanel : UserControl
{
    /// <summary>Raised when the user clicks the welcome buttons. The host (MainWindow)
    /// wires these to opening the new-request flow / collection picker / import wizard
    /// — same actions as the activity rail / top-bar buttons.</summary>
    public event EventHandler? NewRequestRequested;
    public event EventHandler? OpenCollectionRequested;
    public event EventHandler? ImportRequested;

    public NoTabsPanel()
    {
        InitializeComponent();
    }

    private void OnNewRequest_Click(object? sender, RoutedEventArgs e) =>
        NewRequestRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenCollection_Click(object? sender, RoutedEventArgs e) =>
        OpenCollectionRequested?.Invoke(this, EventArgs.Empty);

    private void OnImport_Click(object? sender, RoutedEventArgs e) =>
        ImportRequested?.Invoke(this, EventArgs.Empty);
}
