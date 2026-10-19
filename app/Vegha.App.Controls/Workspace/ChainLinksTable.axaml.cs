using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vegha.App.Controls.Converters;
using Vegha.App.ViewModels.Scriptless;

namespace Vegha.App.Controls.Workspace;

/// <summary>Linked-request cards over a <see cref="ChainLinksEditor"/>: a searchable request
/// picker, the run condition built from pickers, reorder / remove / open.</summary>
public partial class ChainLinksTable : UserControl
{
    public static readonly StyledProperty<ChainLinksEditor?> EditorProperty =
        AvaloniaProperty.Register<ChainLinksTable, ChainLinksEditor?>(nameof(Editor));

    public ChainLinksEditor? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>Every typed term must match the method, name, folder or path.</summary>
    public static readonly AutoCompleteFilterPredicate<object?> CandidateFilter = ChainCandidate.Matches;

    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.5);

    /// <summary>A picker row: method · name · folder, and what the request captures.</summary>
    public static readonly IDataTemplate CandidateTemplate = new FuncDataTemplate<ChainCandidate>((c, _) =>
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,Auto,*"), Margin = new Thickness(0, 1) };
        if (c is null) return grid;
        var method = new TextBlock
        {
            Text = c.MethodLabel,
            FontWeight = FontWeight.Bold,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MethodToBrushConverter.Instance.Convert(c.Method, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as IBrush,
        };
        var name = new TextBlock { Text = c.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock
        {
            Text = string.Join("   ", new[] { c.Folder, c.CapturesText }.Where(s => s.Length > 0)),
            FontSize = 10.5,
            Opacity = 0.6,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(method);
        grid.Children.Add(name);
        grid.Children.Add(detail);
        ToolTip.SetTip(grid, c.Path);
        return grid;
    });

    public ChainLinksTable()
    {
        InitializeComponent();
    }

    /// <summary>Clicking the picker shows the whole list (not just the current name's match),
    /// ready to type into.</summary>
    private void OnPickerGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (!string.IsNullOrEmpty(box.Text)) box.Text = string.Empty;
        Dispatcher.UIThread.Post(() => { if (box.IsKeyboardFocusWithin) box.IsDropDownOpen = true; });
    }

    /// <summary>Leaving without choosing puts the current request's name back.</summary>
    private void OnPickerLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox { DataContext: ChainLinkRow row } box) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsKeyboardFocusWithin) return;
            var name = row.Target?.Name ?? (row.IsBlank ? string.Empty : row.RequestPath);
            if (box.Text != name) box.Text = name;
        });
    }
}
