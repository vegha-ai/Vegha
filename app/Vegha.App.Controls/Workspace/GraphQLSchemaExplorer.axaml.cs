using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// GraphiQL-style schema docs explorer. Selection = navigation: picking a row with a type
/// link pushes that type's page. Keyboard: <c>/</c> focuses search, Backspace pops a level
/// (when not editing the search box).
/// </summary>
public partial class GraphQLSchemaExplorer : UserControl
{
    public GraphQLSchemaExplorer()
    {
        InitializeComponent();
        RowsList.SelectionChanged += OnRowSelected;
        AddHandler(KeyDownEvent, OnExplorerKeyDown, RoutingStrategies.Tunnel);
    }

    private GraphQLSchemaExplorerViewModel? Vm => DataContext as GraphQLSchemaExplorerViewModel;

    private void OnRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is not SchemaExplorerRow row) return;
        // Clear first: navigation rebuilds Rows, and a stale selection index would
        // re-trigger this handler against the new list.
        RowsList.SelectedItem = null;
        if (row.IsNavigable) Vm?.NavigateTo(row.TypeLink);
    }

    private void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        var searchFocused = SearchBox.IsFocused;
        if (e.Key == Key.Back && !searchFocused)
        {
            Vm?.Back();
            e.Handled = true;
        }
        else if (!searchFocused
                 && e.KeyModifiers == KeyModifiers.None
                 && e.PhysicalKey == PhysicalKey.Slash)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && searchFocused)
        {
            SearchBox.Text = string.Empty;
            RowsList.Focus();
            e.Handled = true;
        }
    }
}
