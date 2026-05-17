using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vegha.App.Controls.Converters;

/// <summary>Maps an HTTP method name (or GraphQL operation: QUERY/MUTATION/SUB) to its
/// theme-invariant accent brush. Hex matches the Method*Brush entries in Tokens.axaml,
/// hardcoded here to avoid theme-dictionary lookup issues at databind time.</summary>
public sealed class MethodToBrushConverter : IValueConverter
{
    public static readonly MethodToBrushConverter Instance = new();

    private static readonly IBrush Get     = new SolidColorBrush(Color.Parse("#16A34A")); // green-600
    private static readonly IBrush Post    = new SolidColorBrush(Color.Parse("#CA8A04")); // amber-600
    private static readonly IBrush Put     = new SolidColorBrush(Color.Parse("#2563EB")); // blue-600
    private static readonly IBrush Patch   = new SolidColorBrush(Color.Parse("#7C3AED")); // violet-600
    private static readonly IBrush Delete  = new SolidColorBrush(Color.Parse("#DC2626")); // red-600
    private static readonly IBrush Head    = new SolidColorBrush(Color.Parse("#0891B2")); // cyan-600
    private static readonly IBrush Options = new SolidColorBrush(Color.Parse("#475569")); // slate-600
    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#9CA3AF")); // gray-400

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToUpperInvariant() switch
        {
            "GET" => Get,
            "POST" => Post,
            "PUT" => Put,
            "PATCH" => Patch,
            "DELETE" => Delete,
            "HEAD" => Head,
            "OPTIONS" => Options,
            "QUERY" => Get,
            "MUTATION" => Post,
            "SUB" or "SUBSCRIPTION" => Patch,
            _ => Default,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
