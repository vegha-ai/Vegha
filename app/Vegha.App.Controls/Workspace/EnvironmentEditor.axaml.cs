using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

public partial class EnvironmentEditor : UserControl
{
    public EnvironmentEditor()
    {
        InitializeComponent();
    }

    private void OnRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentTabViewModel vm) return;
        if (sender is Button b && b.Tag is EnvVarRow row) vm.RemoveVariableCommand.Execute(row);
    }
}
