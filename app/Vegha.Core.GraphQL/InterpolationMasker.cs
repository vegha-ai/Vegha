namespace Vegha.Core.GraphQL;

/// <summary>
/// Replaces Vegha <c>{{name}}</c> interpolation tokens with same-length identifier
/// placeholders so GraphQL lexing/parsing sees a syntactically plain document.
/// Interpolation happens before the request is sent, so for analysis purposes a
/// placeholder identifier is the faithful stand-in. The replacement is length-preserving —
/// every diagnostic/completion offset computed on the masked text maps 1:1 onto the
/// original editor text.
/// </summary>
public static class InterpolationMasker
{
    /// <summary>Masks each <c>{{name}}</c> occurrence with <c>_</c>-padded identifier characters
    /// of identical length (e.g. <c>{{host}}</c> → <c>__v0____</c>-style filler). Returns the
    /// input unchanged (same instance) when no token is present.</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text;

        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length - 1)
        {
            if (chars[i] != '{' || chars[i + 1] != '{') { i++; continue; }
            // Find the closing "}}" without crossing newlines or nested braces —
            // mirrors the editor's VarRegex ({{[^{}]*?}}) semantics.
            var close = -1;
            for (var j = i + 2; j < chars.Length - 1; j++)
            {
                var c = chars[j];
                if (c == '{' || c == '}')
                {
                    if (c == '}' && chars[j + 1] == '}') close = j;
                    break;
                }
            }
            if (close < 0) { i += 2; continue; }

            // Replace the whole {{...}} span with identifier chars. A leading letter keeps
            // the masked token a valid GraphQL name even at position 0 of a field.
            chars[i] = 'v';
            for (var k = i + 1; k <= close + 1; k++) chars[k] = '_';
            i = close + 2;
        }
        return new string(chars);
    }
}
