using System.Globalization;
using Avalonia.Data.Converters;

namespace Vegha.App.Controls.Icons;

/// <summary>Converts an IconKey string (as carried by SettingsPageBase.IconKey and similar
/// VM-side properties) into the matching <see cref="IconKind"/> enum value. Unknown / null
/// inputs resolve to <see cref="IconKind.None"/> so XAML bindings never crash.</summary>
public sealed class StringToIconKindConverter : IValueConverter
{
    public static readonly StringToIconKindConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s) && Enum.TryParse<IconKind>(s, ignoreCase: true, out var kind))
            return kind;
        return IconKind.None;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
