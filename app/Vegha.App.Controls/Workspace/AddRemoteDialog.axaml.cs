using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class AddRemoteDialog : Window
{
    public string? RemoteName { get; private set; }
    public string? RemoteUrl { get; private set; }

    public AddRemoteDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    /// <summary>Edit mode locks the name and only allows URL changes.</summary>
    public AddRemoteDialog(string name, string url, bool editMode) : this()
    {
        NameBox.Text = name;
        NameBox.IsReadOnly = editMode;
        NameBox.IsEnabled = !editMode;
        UrlBox.Text = url;
        if (editMode)
        {
            Title = "Edit remote";
            OkButton.Content = "Save";
        }
        Opened += (_, _) => (editMode ? UrlBox : NameBox).Focus();
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) return;
        RemoteName = name;
        RemoteUrl = url;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
