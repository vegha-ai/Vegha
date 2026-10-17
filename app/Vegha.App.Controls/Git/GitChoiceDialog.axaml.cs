using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.Controls.Workspace;

namespace Vegha.App.Controls.Git;

/// <summary>Generic small git prompt: optional list to pick from (e.g. which remote to push
/// to) plus optional monospace detail (e.g. SSH host-key fingerprints to verify).</summary>
public partial class GitChoiceDialog : Window
{
    public string? Choice { get; private set; }

    public GitChoiceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public GitChoiceDialog(string title, string heading, string body, IReadOnlyList<string>? choices, string? detail, string okText) : this()
    {
        Title = title;
        HeadingText.Text = heading;
        BodyText.Text = body;
        OkButton.Content = okText;
        if (choices is { Count: > 0 })
        {
            ChoiceList.ItemsSource = choices;
            ChoiceList.SelectedIndex = 0;
        }
        else ChoiceList.IsVisible = false;
        DetailText.Text = detail;
        DetailText.IsVisible = !string.IsNullOrEmpty(detail);
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        Choice = ChoiceList.SelectedItem as string;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
