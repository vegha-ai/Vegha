using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vegha.App.Controls.Icons;

/// <summary>True → <see cref="IconKind.FolderOpen"/>; false → <see cref="IconKind.Folder"/>.
/// Drives the folder glyph in the Collections tree based on the node's expansion state.</summary>
public sealed class ExpandedToFolderIconConverter : IValueConverter
{
    public static readonly ExpandedToFolderIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? IconKind.FolderOpen : IconKind.Folder;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
