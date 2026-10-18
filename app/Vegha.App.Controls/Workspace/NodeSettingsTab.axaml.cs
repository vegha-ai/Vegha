using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Collection- or folder-level settings, rendered as a workspace tab (Overview /
/// Headers / Vars / Auth / Script / Tests, plus Presets for collections). DataContext is a
/// <c>NodeSettingsTabViewModel</c>; the editing surface is its <c>Props</c>
/// (a shared <c>NodePropertiesViewModel</c>).</summary>
public partial class NodeSettingsTab : UserControl
{
    public NodeSettingsTab()
    {
        InitializeComponent();
    }
}
