using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vegha.App.Controls.Converters;

/// <summary>
/// Converts an environment's optional <c>Color</c> hex string into a <see cref="SolidColorBrush"/>.
/// When the value is null/empty/unparseable, falls back to the hex passed via
/// <c>ConverterParameter</c> (e.g. <c>#9CA3AF</c> for a neutral gray). Distinct from
/// <see cref="HexToBrushConverter"/> which returns <see cref="Brushes.Transparent"/> on
/// miss — that's the wrong default for an environment dot/pill where invisible is worse
/// than dim.
/// </summary>
public sealed class EnvColorConverter : IValueConverter
{
    public static readonly EnvColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c))
            return new SolidColorBrush(c);
        if (parameter is string fallback && Color.TryParse(fallback, out var fc))
            return new SolidColorBrush(fc);
        return new SolidColorBrush(Color.Parse("#9CA3AF"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
