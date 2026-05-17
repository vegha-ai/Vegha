using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class ConfirmDiscardDialog : Window
{
    public bool RecycleNewFiles { get; private set; } = true;

    public ConfirmDiscardDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public ConfirmDiscardDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        RecycleNewFiles = RecycleBinBox.IsChecked ?? true;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
