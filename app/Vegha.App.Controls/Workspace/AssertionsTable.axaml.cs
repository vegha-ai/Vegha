using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Scriptless assertion rows (<see cref="Vegha.App.ViewModels.Scriptless.AssertionRow"/>).
/// Used by the request Tests tab and folder / collection settings.</summary>
public partial class AssertionsTable : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AssertionsTable, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<AssertionsTable, ICommand?>(nameof(RemoveCommand));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public AssertionsTable()
    {
        InitializeComponent();
    }
}
