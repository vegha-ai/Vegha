using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Runner;

namespace Vegha.App.Controls.Shell;

public partial class RunnerPanel : UserControl
{
    public RunnerPanel()
    {
        InitializeComponent();
    }

    private void OnRun_Click(object? s, RoutedEventArgs e)
    {
        if (DataContext is not RunnerSidebarViewModel vm) return;
        if (s is not Control c || c.Tag is not CollectionRootViewModel root) return;
        vm.NewRunCommand.Execute(root);
    }
}
