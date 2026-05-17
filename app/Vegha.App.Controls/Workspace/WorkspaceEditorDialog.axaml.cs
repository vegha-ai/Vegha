using Avalonia.Controls;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Edit Workspace" window. Hosts the same <see cref="WorkspaceEditor"/>
/// control we used to render in a main-area tab, but inside a dialog so workspace editing
/// doesn't steal the tab strip / request workspace. The host (MainWindow) fills the
/// editor's <see cref="WorkspaceTabViewModel"/> with collections + envs + callback hooks
/// before <c>ShowDialog</c>.</summary>
public partial class WorkspaceEditorDialog : Window
{
    public WorkspaceEditorDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    /// <summary>Convenience setter: bind the editor view to a fully-prepared tab VM.</summary>
    public WorkspaceTabViewModel? EditorContext
    {
        get => Editor.DataContext as WorkspaceTabViewModel;
        set => Editor.DataContext = value;
    }
}
