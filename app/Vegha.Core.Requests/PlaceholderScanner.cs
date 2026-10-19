namespace Vegha.Core.Requests;

/// <summary>
/// Finds <c>{{name}}</c> placeholders that survived interpolation. Those go out as literal
/// text and almost always cause a confusing server-side error, so senders surface them.
/// </summary>
public static class PlaceholderScanner
{
    /// <summary>Unique unresolved names across the URL, headers and body, in discovery order.</summary>
    public static IReadOnlyList<string> Collect(
        string url,
        IEnumerable<KeyValuePair<string, string>> headers,
        string? body)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Scan(url);
        foreach (var (n, v) in headers) { Scan(n); Scan(v); }
        if (body is not null) Scan(body);
        return found;

        void Scan(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return;
            var i = 0;
            while (i < text.Length - 1)
            {
                if (text[i] == '{' && text[i + 1] == '{')
                {
                    var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    var name = text.Substring(i + 2, close - (i + 2)).Trim();
                    if (name.Length > 0 && seen.Add(name)) found.Add(name);
                    i = close + 2;
                }
                else i++;
            }
        }
    }
}
