using Avalonia.Controls;
using Avalonia.Input;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class SearchPaletteWindow : Window
{
    public SearchPaletteWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("QueryBox")?.Focus();
        // Esc closes; Enter activates the selected row.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && DataContext is SearchPaletteViewModel vm && vm.Selected is not null)
            {
                vm.ActivateCommand.Execute(vm.Selected);
                Close();
                e.Handled = true;
            }
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
