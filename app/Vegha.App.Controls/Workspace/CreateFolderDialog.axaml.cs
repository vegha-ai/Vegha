using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "New Folder" name prompt. Returns the entered name via <see cref="Result"/>
/// (null on cancel). The caller is responsible for sanitizing the name to filesystem rules
/// and writing the folder + folder.bru marker.</summary>
public partial class CreateFolderDialog : Window
{
    public string? Result { get; private set; }

    public CreateFolderDialog() : this(string.Empty) { }

    public CreateFolderDialog(string suggestedName)
    {
        InitializeComponent();
        NameBox.Text = suggestedName;
        NameBox.SelectAll();
        // Focus after load so the prompt is immediately typeable.
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnCreate_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return;
        Result = name;
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
