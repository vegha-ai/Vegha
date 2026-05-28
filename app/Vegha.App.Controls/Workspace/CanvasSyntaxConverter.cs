using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vegha.App.Controls.Workspace;

/// <summary>Maps the VM's <c>ResponseCanvasSyntaxKind</c> string ("json" / "xml" /
/// "none") to <see cref="CanvasTextView.Syntax"/>. Keeps the VM Avalonia-free
/// while the view drives the canvas-painted highlighter.</summary>
public sealed class CanvasSyntaxConverter : IValueConverter
{
    public static readonly CanvasSyntaxConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.Equals(s, "json", StringComparison.OrdinalIgnoreCase)) return CanvasTextView.Syntax.Json;
        if (string.Equals(s, "xml", StringComparison.OrdinalIgnoreCase))  return CanvasTextView.Syntax.Xml;
        return CanvasTextView.Syntax.None;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
