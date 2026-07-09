using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Collection-level settings, rendered as a workspace tab (Overview / Headers /
/// Vars / Auth / Script / Tests / Presets). DataContext is a
/// <c>CollectionSettingsTabViewModel</c>; the editing surface is its <c>Props</c>
/// (a shared <c>NodePropertiesViewModel</c>).</summary>
public partial class CollectionSettingsTab : UserControl
{
    public CollectionSettingsTab()
    {
        InitializeComponent();
    }
}
