using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vegha.App.Controls.Converters;

/// <summary>Maps an HTTP status code to the active theme's semantic brush so the
/// response status reads its outcome at a glance instead of always rendering green:
/// 2xx → StatusOk, 3xx → StatusInfo, 4xx → StatusWarn, 5xx (and 0 / unknown) → StatusErr.
///
/// Resolves the brush from the application resource dictionary using the current
/// <see cref="ThemeVariant"/>, so each theme contributes its own tuned semantic hue.
/// The binding re-evaluates whenever the bound status code changes (i.e. on every
/// response); a theme switched while a response is on screen is picked up on the next
/// request.</summary>
public sealed class StatusCategoryToBrushConverter : IValueConverter
{
    public static readonly StatusCategoryToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var code = value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };

        var key = code switch
        {
            >= 200 and < 300 => "StatusOkBrush",
            >= 300 and < 400 => "StatusInfoBrush",
            >= 400 and < 500 => "StatusWarnBrush",
            _ => "StatusErrBrush", // 5xx, 0 (no response / network error), and anything unexpected
        };

        return ResolveBrush(key);
    }

    private static IBrush? ResolveBrush(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
        {
            return brush;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
