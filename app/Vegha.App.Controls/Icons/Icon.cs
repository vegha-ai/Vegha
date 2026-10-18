using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Vegha.App.Controls.Icons;

/// <summary>Renders an inline-SVG icon at the requested pixel size. Stroke color follows
/// <see cref="Foreground"/>; geometry comes from <see cref="IconLibrary"/>. Drops a
/// <see cref="Canvas"/> wrapped in a <see cref="Viewbox"/> so the 24-unit baseline scales cleanly.</summary>
public sealed class Icon : ContentControl
{
    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<Icon, IconKind>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), defaultValue: 16);

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    // Shapes of the current glyph + the brush they were painted with, so a Foreground-only
    // change (hover, selection, theme switch) recolors in place instead of rebuilding.
    private Canvas? _canvas;
    private IBrush? _builtBrush;
    // Kind changed since the last build. Building is deferred until the icon is in the visual
    // tree: XAML sets Kind, Size and an inherited Foreground one after another, and building
    // eagerly re-parsed the geometry for every one of those.
    private bool _dirty = true;

    public Icon()
    {
        Width = 16;
        Height = 16;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SizeProperty)
        {
            Width = Size;
            Height = Size;
        }
        else if (change.Property == KindProperty)
        {
            _dirty = true;
            if (VisualRoot is not null) Rebuild();
        }
        else if (change.Property == ForegroundProperty && !_dirty)
        {
            Recolor();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_dirty) Rebuild();
        else if (!ReferenceEquals(_builtBrush, Foreground ?? Brushes.White)) Recolor();
    }

    private void Rebuild()
    {
        _dirty = false;
        _builtBrush = Foreground ?? Brushes.White;
        _canvas = new Canvas { Width = 24, Height = 24, Background = Brushes.Transparent };
        foreach (var shape in IconLibrary.Build(Kind, _builtBrush))
        {
            _canvas.Children.Add(shape);
        }

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = _canvas,
        };
    }

    /// <summary>Swaps the old foreground for the new one on every shape that was painted with
    /// it. Shapes with their own fixed colors (e.g. a transparent fill) are left alone, exactly
    /// as a fresh build would leave them.</summary>
    private void Recolor()
    {
        if (_canvas is null) return;
        var brush = Foreground ?? Brushes.White;
        foreach (var child in _canvas.Children)
        {
            if (child is not Shape shape) continue;
            if (ReferenceEquals(shape.Stroke, _builtBrush)) shape.Stroke = brush;
            if (ReferenceEquals(shape.Fill, _builtBrush)) shape.Fill = brush;
        }
        _builtBrush = brush;
    }
}
