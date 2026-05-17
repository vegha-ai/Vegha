using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Remove Collection" confirmation dialog. Surfaces the collection's name
/// and on-disk path so the user can verify what's being detached, and clarifies that the
/// files themselves stay on disk (only the in-app reference is dropped). Returns <c>true</c>
/// when the user confirms via the Remove button; <c>false</c> for Cancel / window close.</summary>
public partial class RemoveCollectionDialog : Window
{
    public RemoveCollectionDialog() : this(string.Empty, string.Empty) { }

    public RemoveCollectionDialog(string collectionName, string folderPath)
    {
        InitializeComponent();
        NameLabel.Text = string.IsNullOrEmpty(collectionName) ? "(unnamed)" : collectionName;
        PathLabel.Text = folderPath ?? string.Empty;
    }

    private void OnRemove_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
