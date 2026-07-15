using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Checkbox query-builder tree (see <see cref="Vegha.App.ViewModels.GraphQLQueryBuilderViewModel"/>).
/// All behavior lives in the VM; the view is pure templates.</summary>
public partial class GraphQLQueryBuilderView : UserControl
{
    public GraphQLQueryBuilderView()
    {
        InitializeComponent();
    }
}
