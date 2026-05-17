using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vegha.App.Controls.Converters;

/// <summary>
/// Maps a boolean "is the secret visible?" flag to the <c>PasswordChar</c> property of a
/// TextBox. When the flag is true, returns the null char (default — TextBox shows text
/// verbatim). When false, returns the bullet glyph used by the rest of the password
/// fields in the app. Used by the OAuth2 Client Secret field's eye-icon toggle.
/// </summary>
public sealed class BoolToPasswordCharConverter : IValueConverter
{
    public static readonly BoolToPasswordCharConverter Instance = new();

    /// <summary>Glyph used when the field is masked. Matches the literal in the other
    /// PasswordChar="•" sites in AuthEditor.axaml so the visual is consistent.</summary>
    public char MaskedChar { get; init; } = '•';

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // value=true → show as plaintext (no mask). value=false (or anything else) → mask.
        return value is bool b && b ? '\0' : MaskedChar;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
