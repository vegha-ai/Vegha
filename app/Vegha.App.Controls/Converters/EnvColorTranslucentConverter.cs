using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vegha.App.Controls.Converters;

/// <summary>
/// Converts an environment's hex color to a translucent <see cref="SolidColorBrush"/>
/// suitable for a tinted pill background (env color at low alpha). When the value is
/// null/empty/unparseable, falls back to the hex passed via <c>ConverterParameter</c>,
/// also rendered at low alpha. The default alpha is <c>0x26</c> (~15%); pass a custom
/// alpha by encoding it in the parameter as <c>#AARRGGBB</c>.
/// </summary>
public sealed class EnvColorTranslucentConverter : IValueConverter
{
    public static readonly EnvColorTranslucentConverter Instance = new();

    private const byte DefaultAlpha = 0x26;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c))
            return new SolidColorBrush(Color.FromArgb(DefaultAlpha, c.R, c.G, c.B));
        if (parameter is string fallback && Color.TryParse(fallback, out var fc))
            return new SolidColorBrush(Color.FromArgb(DefaultAlpha, fc.R, fc.G, fc.B));
        return new SolidColorBrush(Color.FromArgb(DefaultAlpha, 0x9C, 0xA3, 0xAF));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
