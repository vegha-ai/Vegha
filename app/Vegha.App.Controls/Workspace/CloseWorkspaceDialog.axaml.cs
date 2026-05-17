using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Vegha.App.Controls.Workspace;

/// <summary>Confirmation dialog for "Close workspace". Closes with a bool result:
/// true = user confirmed close, false = canceled.</summary>
public partial class CloseWorkspaceDialog : Window
{
    public CloseWorkspaceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public CloseWorkspaceDialog(string workspaceName, string workspacePath)
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
        NameText.Text = workspaceName;
        PathText.Text = workspacePath;

        // "Are you sure you want to close workspace <Bold>Name</Bold>?"
        PromptText.Inlines!.Add(new Run("Are you sure you want to close workspace "));
        PromptText.Inlines!.Add(new Run(workspaceName) { FontWeight = FontWeight.Bold });
        PromptText.Inlines!.Add(new Run("?"));
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close(true);

    /// <summary>Repurposes the same shell for "Delete collection" — the prompt becomes a
    /// destructive warning ("will be permanently deleted from disk") so the user is clearly
    /// warned the action is irreversible.</summary>
    public void SetPromptForCollectionDelete()
    {
        var name = NameText.Text ?? "this collection";
        PromptText.Inlines!.Clear();
        PromptText.Inlines!.Add(new Run("Permanently delete collection "));
        PromptText.Inlines!.Add(new Run(name) { FontWeight = FontWeight.Bold });
        PromptText.Inlines!.Add(new Run("?"));
        FootnoteText.Text = "This will remove the folder and ALL files inside it from disk. This cannot be undone.";
        ConfirmButton.Content = "Delete";
    }

    /// <summary>Same as <see cref="SetPromptForCollectionDelete"/> but tuned for environments —
    /// the path row in this case is the workspace folder containing the env file.</summary>
    public void SetPromptForEnvDelete()
    {
        var name = NameText.Text ?? "this environment";
        PromptText.Inlines!.Clear();
        PromptText.Inlines!.Add(new Run("Delete environment "));
        PromptText.Inlines!.Add(new Run(name) { FontWeight = FontWeight.Bold });
        PromptText.Inlines!.Add(new Run("?"));
        FootnoteText.Text = "The environment file will be removed from this workspace's environments/ folder.";
        ConfirmButton.Content = "Delete";
    }
}
