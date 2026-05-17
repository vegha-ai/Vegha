using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vegha.App.Controls.Converters;

/// <summary>Converts a `#RRGGBB` or `#AARRGGBB` hex string to a <see cref="SolidColorBrush"/>.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && Color.TryParse(s, out var color))
            return new SolidColorBrush(color);
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multi-binding equality: returns <c>true</c> when the two input values
/// are <see cref="object.Equals(object?, object?)"/>-equal, otherwise <c>false</c>.
/// Avalonia's <c>ObjectConverters.Equal</c> is <see cref="IValueConverter"/>-only;
/// this fills the multi-binding gap used by the Appearance variant cards to
/// flag the selected entry's checkmark.</summary>
public sealed class EqualMultiConverter : IMultiValueConverter
{
    public static readonly EqualMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2 && Equals(values[0], values[1]);
}

/// <summary>Multi-binding companion to <see cref="ObjectConverters.Equal"/>: returns the
/// <see cref="Avalonia.Application.Resources"/> brush named <c>AccentBrush</c> when the
/// two inputs are equal and <see cref="Brushes.Transparent"/> otherwise. Used by the
/// Appearance theme cards to ring the currently-active variant with the accent color —
/// avoids carving out a code-behind helper or a Style trigger per card.</summary>
public sealed class EqualToBrushConverter : IMultiValueConverter
{
    public static readonly EqualToBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return Brushes.Transparent;
        if (!Equals(values[0], values[1])) return Brushes.Transparent;
        if (Application.Current?.Resources.TryGetResource("AccentBrush", null, out var brush) == true
            && brush is IBrush b)
            return b;
        return Brushes.Transparent;
    }
}

/// <summary>Multiplies a 0..1 ratio by the (string-parsed) parameter total to yield a width in pixels.</summary>
public sealed class RatioToWidthConverter : IValueConverter
{
    public static readonly RatioToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ratio && parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            return Math.Max(0, ratio * total);
        return 0d;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
