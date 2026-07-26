using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Collection- and folder-level settings, rendered as a workspace tab (Overview /
/// Headers / Vars / Auth / Script / Tests, plus Presets for collections). DataContext is a
/// <c>NodeSettingsTabViewModel</c>; the editing surface is its <c>Props</c> (a shared
/// <c>NodePropertiesViewModel</c>, whose Auth is the same <c>AuthEditor</c> the request tab
/// hosts).</summary>
public partial class NodeSettingsTab : UserControl
{
    public NodeSettingsTab()
    {
        InitializeComponent();
    }
}
