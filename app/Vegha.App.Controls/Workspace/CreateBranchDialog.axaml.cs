using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class CreateBranchDialog : Window
{
    public string? ResultName { get; private set; }
    public bool CheckoutAfterCreate { get; private set; } = true;

    public CreateBranchDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public CreateBranchDialog(string baseBranch) : this()
    {
        BaseLabel.Text = $"Based on {baseBranch} (current).";
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        ResultName = name;
        CheckoutAfterCreate = CheckoutBox.IsChecked ?? true;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        ResultName = null;
        Close(false);
    }
}
