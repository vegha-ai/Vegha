using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Vegha.Tests.UI;

/// <summary>Guards the compact input ControlThemes (Themes/CompactInputs.axaml): the Fluent
/// replacements must produce 14px CheckBox boxes / RadioButton rings and thin stroke tree
/// chevrons. Sizes are asserted from realized bounds — Fluent's inline template sizes are
/// local values, so a regression to stock Fluent shows up here as 20px parts.</summary>
public class CompactInputStyleDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    public CompactInputStyleDiagnosticTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void CheckBox_And_RadioButton_Use_Compact_Marks()
    {
        var plain = new CheckBox { Content = "plain" };
        var compact = new CheckBox { Classes = { "compact" } };
        var radio = new RadioButton { Content = "radio" };

        var panel = new StackPanel { Spacing = 4, Children = { plain, compact, radio } };
        var window = new Window { Content = panel, Width = 300, Height = 200 };
        window.Show();
        window.UpdateLayout();

        var plainBox = FindPart<Border>(plain, "NormalRectangle");
        var compactBox = FindPart<Border>(compact, "NormalRectangle");
        var ring = FindPart<Ellipse>(radio, "OuterEllipse");
        var dot = FindPart<Ellipse>(radio, "CheckGlyph");

        _output.WriteLine($"plain box={plainBox.Bounds} compact box={compactBox.Bounds} ring={ring.Bounds} dot={dot.Bounds}");
        _output.WriteLine($"plain={plain.Bounds} compact={compact.Bounds} radio={radio.Bounds}");

        Assert.Equal(14, plainBox.Bounds.Width);
        Assert.Equal(14, plainBox.Bounds.Height);
        Assert.Equal(14, compactBox.Bounds.Width);
        Assert.Equal(14, ring.Bounds.Width);
        Assert.Equal(6, dot.Bounds.Width);
        // Labeled controls keep a 20px min row; the mark strip must not force Fluent's 32px.
        Assert.Equal(20, plain.Bounds.Height);
        Assert.Equal(20, radio.Bounds.Height);
    }

    [AvaloniaFact]
    public void TreeViewItem_Chevron_Is_Thin_Stroke()
    {
        var child = new TreeViewItem { Header = "child" };
        var item = new TreeViewItem { Header = "parent", Items = { child } };
        var tree = new TreeView { Items = { item } };
        var window = new Window { Content = tree, Width = 300, Height = 200 };
        window.Show();
        window.UpdateLayout();

        var toggle = FindPart<ToggleButton>(item, "PART_ExpandCollapseChevron");
        var path = FindPart<Avalonia.Controls.Shapes.Path>(item, "ChevronPath");

        _output.WriteLine($"toggle={toggle.Bounds} path stroke={path.Stroke} fill={path.Fill} thickness={path.StrokeThickness}");

        Assert.Equal(14, toggle.Bounds.Width);
        Assert.NotNull(path.Stroke);   // stroke chevron, not Fluent's filled glyph
        Assert.Null(path.Fill);
        Assert.Equal(1.5, path.StrokeThickness);
    }

    private static T FindPart<T>(Visual root, string name) where T : Visual
    {
        foreach (var v in root.GetVisualDescendants())
        {
            if (v is T t && (v as StyledElement)?.Name == name) return t;
        }
        throw new Xunit.Sdk.XunitException($"Template part '{name}' ({typeof(T).Name}) not found.");
    }
}
