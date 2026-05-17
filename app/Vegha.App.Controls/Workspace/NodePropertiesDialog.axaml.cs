using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class NodePropertiesDialog : Window
{
    public NodePropertiesDialog()
    {
        InitializeComponent();
    }

    private void OnSave_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRemoveVariable_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodePropertiesViewModel vm) return;
        if (sender is Button b && b.Tag is KvEntry row) vm.RemoveVariableCommand.Execute(row);
    }

    private void OnRemoveHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodePropertiesViewModel vm) return;
        if (sender is Button b && b.Tag is KvEntry row) vm.RemoveHeaderCommand.Execute(row);
    }
}
