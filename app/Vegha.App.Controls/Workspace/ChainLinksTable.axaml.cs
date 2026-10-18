using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>Linked-request rows (<see cref="Vegha.App.ViewModels.Scriptless.ChainLinkRow"/>).
/// <see cref="RequestPaths"/> feeds the request picker.</summary>
public partial class ChainLinksTable : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ChainLinksTable, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IEnumerable?> RequestPathsProperty =
        AvaloniaProperty.Register<ChainLinksTable, IEnumerable?>(nameof(RequestPaths));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChainLinksTable, ICommand?>(nameof(RemoveCommand));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IEnumerable? RequestPaths
    {
        get => GetValue(RequestPathsProperty);
        set => SetValue(RequestPathsProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ChainLinksTable()
    {
        InitializeComponent();
    }
}
