using Avalonia.Controls;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Subscription frames timeline. Selecting a row shows that frame's payload in the
/// response body viewer (via the VM command) — selection is cleared right away so the same
/// row can be re-picked after newer frames arrive.</summary>
public partial class GraphQLSubscriptionView : UserControl
{
    public GraphQLSubscriptionView()
    {
        InitializeComponent();
        FramesList.SelectionChanged += (_, _) =>
        {
            if (FramesList.SelectedItem is not GraphQLSubscriptionFrame frame) return;
            FramesList.SelectedItem = null;
            (DataContext as RequestEditorViewModel)?.ShowSubscriptionFrameCommand.Execute(frame);
        };
    }
}
