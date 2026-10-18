using Avalonia.Controls;

namespace Vegha.App.Controls.Shell;

/// <summary>Session runtime-variables list (Environments panel, Tests-tab flyout). Behavior
/// lives in <see cref="Vegha.App.ViewModels.Scriptless.RuntimeVariablesViewModel"/>.</summary>
public partial class RuntimeVariablesList : UserControl
{
    public RuntimeVariablesList()
    {
        InitializeComponent();
    }
}
