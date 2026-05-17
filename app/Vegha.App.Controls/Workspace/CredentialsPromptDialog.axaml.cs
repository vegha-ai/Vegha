using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class CredentialsPromptDialog : Window
{
    public string? Username { get; private set; }
    public string? Secret { get; private set; }
    public bool Remember { get; private set; }

    public CredentialsPromptDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public CredentialsPromptDialog(string host, string? usernameHint) : this()
    {
        HostLabel.Text = host;
        if (!string.IsNullOrEmpty(usernameHint))
            UserBox.Text = usernameHint;
        Opened += (_, _) =>
        {
            if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
            else SecretBox.Focus();
        };
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var user = UserBox.Text?.Trim();
        var secret = SecretBox.Text;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(secret)) return;
        Username = user;
        Secret = secret;
        Remember = RememberBox.IsChecked ?? false;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
