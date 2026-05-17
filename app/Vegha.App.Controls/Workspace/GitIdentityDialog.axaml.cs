using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class GitIdentityDialog : Window
{
    public string? UserName { get; private set; }
    public string? UserEmail { get; private set; }

    public GitIdentityDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public GitIdentityDialog(string currentName, string currentEmail) : this()
    {
        NameBox.Text = currentName;
        EmailBox.Text = currentEmail;
        Opened += (_, _) => (string.IsNullOrEmpty(currentName) ? NameBox : EmailBox).Focus();
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var email = EmailBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email)) return;
        UserName = name;
        UserEmail = email;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
