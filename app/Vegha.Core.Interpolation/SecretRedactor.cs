namespace Vegha.Core.Interpolation;

/// <summary>
/// Masks resolved secret values before they reach a surface the user can copy or that gets
/// persisted (code snippets, request previews, history). Operates on the already-interpolated
/// text: each known secret value is replaced with a bullet placeholder.
/// </summary>
public static class SecretRedactor
{
    /// <summary>Bullet placeholder substituted for a secret value.</summary>
    public const string Mask = "•••";

    /// <summary>Values shorter than this are skipped — replacing a 1-3 char string would
    /// pepper the output with masks at every coincidental occurrence (e.g. a secret value
    /// of "1" would mask every digit 1).</summary>
    private const int MinLength = 4;

    /// <summary>Replaces every occurrence of each secret value in <paramref name="text"/>
    /// with <see cref="Mask"/>. Longer values are masked first so a secret that contains
    /// a shorter secret as a substring still redacts cleanly.</summary>
    public static string Redact(string text, IEnumerable<string> secretValues)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var ordered = secretValues
            .Where(v => !string.IsNullOrEmpty(v) && v.Length >= MinLength)
            .Distinct()
            .OrderByDescending(v => v.Length);
        foreach (var value in ordered)
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        return text;
    }
}
