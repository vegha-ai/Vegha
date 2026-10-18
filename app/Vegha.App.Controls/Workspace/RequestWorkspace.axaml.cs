using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class RequestWorkspace : UserControl
{
    /// <summary>Default height of the Tests tab's JavaScript section when first expanded.</summary>
    private const double DefaultTestsScriptHeight = 300;
    private const double MinTestsScriptHeight = 140;
    private const double MinTablesHeight = 120;

    /// <summary>Last dragged height of the JavaScript section — shared by every tab for the
    /// session, so resizing once sticks as you move between requests.</summary>
    private static double s_testsScriptHeight = DefaultTestsScriptHeight;

    private RequestEditorViewModel? _vm;

    public RequestWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as RequestEditorViewModel);
        if (this.FindControl<GridSplitter>("TestsScriptSplitter") is { } splitter)
            splitter.DragCompleted += (_, _) => RememberTestsScriptHeight();
    }

    private void AttachViewModel(RequestEditorViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyTestsScriptLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RequestEditorViewModel.IsTestsScriptExpanded)) return;
        // Leaving the expanded state: keep whatever height the user had dragged to.
        if (_vm is { IsTestsScriptExpanded: false }) RememberTestsScriptHeight();
        ApplyTestsScriptLayout();
    }

    /// <summary>Collapsed: the section is just its header (Auto row). Expanded: a fixed-pixel row
    /// the splitter resizes against the tables' star row.</summary>
    private void ApplyTestsScriptLayout()
    {
        if (this.FindControl<Grid>("TestsSplitGrid") is not { } grid || grid.RowDefinitions.Count < 3) return;
        var tables = grid.RowDefinitions[0];
        var section = grid.RowDefinitions[2];
        if (_vm?.IsTestsScriptExpanded == true)
        {
            tables.MinHeight = MinTablesHeight;
            section.MinHeight = MinTestsScriptHeight;
            section.Height = new GridLength(s_testsScriptHeight, GridUnitType.Pixel);
        }
        else
        {
            tables.MinHeight = 0;
            section.MinHeight = 0;
            section.Height = GridLength.Auto;
        }
    }

    private void RememberTestsScriptHeight()
    {
        if (this.FindControl<Grid>("TestsSplitGrid") is not { } grid || grid.RowDefinitions.Count < 3) return;
        var section = grid.RowDefinitions[2];
        if (section.Height.IsAbsolute && section.ActualHeight >= MinTestsScriptHeight)
            s_testsScriptHeight = section.ActualHeight;
    }

    private void OnTestsScriptSplitterDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        s_testsScriptHeight = DefaultTestsScriptHeight;
        ApplyTestsScriptLayout();
    }
}

/// <summary>JavaScript-tests header border: a top rule only while collapsed — when expanded,
/// the draggable splitter above it is the divider.</summary>
public sealed class TestsHeaderBorderConverter : IValueConverter
{
    public static readonly TestsHeaderBorderConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(0) : new Thickness(0, 1, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
