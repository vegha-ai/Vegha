using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vegha.App.Controls.Converters;

/// <summary>Multiplies a TreeViewItem's <c>Level</c> by a per-level pixel indent
/// (default 10) so we control how much each nesting step shifts the row.</summary>
public sealed class LevelToIndentConverter : IValueConverter
{
    public static readonly LevelToIndentConverter Instance = new();

    public double IndentPx { get; set; } = 10;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value switch
        {
            int i => i,
            double d => (int)d,
            _ => 0,
        };
        return Math.Max(0, level) * IndentPx;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
