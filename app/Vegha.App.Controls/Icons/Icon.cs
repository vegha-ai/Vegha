using Avalonia;
using Avalonia.Controls;
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

    public Icon()
    {
        Width = 16;
        Height = 16;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == SizeProperty || change.Property == ForegroundProperty)
            Refresh();
    }

    private void Refresh()
    {
        Width = Size;
        Height = Size;

        var canvas = new Canvas { Width = 24, Height = 24, Background = Brushes.Transparent };
        var brush = Foreground ?? Brushes.White;
        foreach (var shape in IconLibrary.Build(Kind, brush))
        {
            canvas.Children.Add(shape);
        }

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }
}
