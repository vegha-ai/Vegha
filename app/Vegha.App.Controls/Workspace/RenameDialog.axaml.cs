using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

/// <summary>Generic single-line rename prompt. Returns the new name via
/// <see cref="ResultName"/> when the user confirms (or via the dialog's bool result, true).
/// Used for renaming workspaces, collections, and environments.</summary>
public partial class RenameDialog : Window
{
    public string? ResultName { get; private set; }

    public RenameDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public RenameDialog(string title, string label, string currentName)
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
        Title = title;
        LabelText.Text = label;
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        ResultName = name;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        ResultName = null;
        Close(false);
    }
}
