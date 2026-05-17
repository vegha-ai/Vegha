namespace Vegha.Core.Domain;

/// <summary>
/// Parses and serializes a key/value collection between text form ("bulk edit" mode)
/// and the structured <see cref="KvPair"/> shape. Mirrors Bruno's bulk-edit pane.
///
/// Accepts both common syntaxes auto-detected on the first non-empty line:
///   • HTTP-header style:   <c>Key: Value</c>            (preferred for Headers)
///   • Form / param style:  <c>key=value</c>             (preferred for Params)
/// Mixed lines within a single block are tolerated — each line is parsed independently
/// using whichever separator appears first.
///
/// Per-row modifiers:
///   • Leading <c>~</c>     → row is disabled (Bruno parity)
///   • Leading <c>#</c>     → line is a comment, skipped entirely
///   • Blank lines          → ignored
///
/// Round-trip stability: <see cref="Format"/> uses <c>Key: Value</c> by default; toggling
/// to bulk and back leaves the rows unchanged.
/// </summary>
public static class KvBulkEditParser
{
    /// <summary>Parses <paramref name="text"/> into a list of KvPairs. Always succeeds —
    /// malformed lines (no separator) are kept as Name-only entries with empty Value.</summary>
    public static IReadOnlyList<KvPair> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<KvPair>();
        var result = new List<KvPair>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Strip leading whitespace for modifier detection only — value content keeps
            // its own internal whitespace.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

            var enabled = true;
            if (trimmed.StartsWith("~", StringComparison.Ordinal))
            {
                enabled = false;
                trimmed = trimmed[1..].TrimStart();
            }

            // Pick the separator: whichever of ":" or "=" comes first wins. Falls back to
            // "name only, no value" when neither is present.
            var colon = trimmed.IndexOf(':');
            var equals = trimmed.IndexOf('=');
            int sep;
            if (colon < 0) sep = equals;
            else if (equals < 0) sep = colon;
            else sep = Math.Min(colon, equals);

            string name, value;
            if (sep <= 0)
            {
                name = trimmed.Trim();
                value = string.Empty;
            }
            else
            {
                name = trimmed[..sep].Trim();
                value = trimmed[(sep + 1)..].TrimStart();
            }

            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new KvPair(name, value, enabled));
        }
        return result;
    }

    /// <summary>Serializes <paramref name="pairs"/> into the bulk-edit textual form.
    /// Uses <c>Key: Value</c> with a leading <c>~</c> for disabled rows. Pairs with empty
    /// Name are skipped — they'd round-trip as comments otherwise.</summary>
    public static string Format(IEnumerable<KvPair> pairs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in pairs)
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            if (!p.Enabled) sb.Append('~');
            sb.Append(p.Name);
            sb.Append(": ");
            sb.AppendLine(p.Value ?? string.Empty);
        }
        return sb.ToString();
    }
}
