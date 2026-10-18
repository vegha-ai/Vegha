using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Vegha.Core.Scripting.Testing;

/// <summary>Immutable view of one HTTP response that the scriptless capture / assertion engine
/// evaluates against. Parses the body lazily (JSON first, XML on demand) and indexes headers +
/// <c>Set-Cookie</c> cookies case-insensitively.</summary>
public sealed class ResponseSnapshot
{
    private readonly Lazy<(bool Ok, JsonNode? Node)> _json;
    private readonly Lazy<XDocument?> _xml;

    public int Status { get; }
    public string StatusText { get; }
    public string Body { get; }
    public long ElapsedMilliseconds { get; }
    public string Url { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
    public IReadOnlyDictionary<string, string> Cookies { get; }

    public ResponseSnapshot(
        int status,
        string? statusText,
        string? body,
        long elapsedMilliseconds,
        IEnumerable<KeyValuePair<string, string>>? headers,
        string? url = null)
    {
        Status = status;
        StatusText = statusText ?? string.Empty;
        Body = body ?? string.Empty;
        ElapsedMilliseconds = elapsedMilliseconds;
        Url = url ?? string.Empty;
        Headers = (headers ?? Array.Empty<KeyValuePair<string, string>>()).ToList();
        Cookies = ParseCookies(Headers);
        _json = new Lazy<(bool, JsonNode?)>(ParseJson);
        _xml = new Lazy<XDocument?>(ParseXml);
    }

    /// <summary>True when the body parsed as JSON (a literal <c>null</c> body counts).</summary>
    public bool IsJson => _json.Value.Ok;

    /// <summary>The parsed JSON body, or null when it isn't JSON (check <see cref="IsJson"/>).</summary>
    public JsonNode? Json => _json.Value.Node;

    /// <summary>The parsed XML body, or null when it isn't well-formed XML.</summary>
    public XDocument? Xml => _xml.Value;

    /// <summary>First header value with the given name (case-insensitive); multiple values of
    /// the same header are joined with <c>", "</c> like HTTP folding.</summary>
    public string? GetHeader(string name)
    {
        string? found = null;
        foreach (var (k, v) in Headers)
        {
            if (!string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) continue;
            found = found is null ? v : found + ", " + v;
        }
        return found;
    }

    public string? ContentType => GetHeader("Content-Type");

    private (bool, JsonNode?) ParseJson()
    {
        var trimmed = Body.AsSpan().TrimStart();
        if (trimmed.IsEmpty) return (false, null);
        try
        {
            return (true, JsonNode.Parse(Body, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private XDocument? ParseXml()
    {
        var trimmed = Body.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '<') return null;
        try { return XDocument.Parse(Body); }
        catch { return null; }
    }

    private static IReadOnlyDictionary<string, string> ParseCookies(
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            if (!string.Equals(k, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            var first = v.Split(';', 2)[0];
            var eq = first.IndexOf('=');
            if (eq <= 0) continue;
            cookies[first[..eq].Trim()] = first[(eq + 1)..].Trim();
        }
        return cookies;
    }
}

/// <summary>A value pulled out of a response. Distinguishes "not there" (<see cref="Exists"/>
/// false — JS <c>undefined</c>) from JSON <c>null</c> (<see cref="Exists"/> true, <see cref="Node"/> null).</summary>
public readonly record struct ResolvedValue(bool Exists, JsonNode? Node)
{
    public static readonly ResolvedValue Missing = new(false, null);
    public static ResolvedValue Null => new(true, null);

    public static ResolvedValue Of(JsonNode? node) => new(true, node);
    public static ResolvedValue Of(string? s) => s is null ? Missing : new(true, JsonValue.Create(s));
    public static ResolvedValue Of(long n) => new(true, JsonValue.Create(n));
    public static ResolvedValue Of(double n) => new(true, JsonValue.Create(n));
    public static ResolvedValue Of(bool b) => new(true, JsonValue.Create(b));

    public bool IsNull => Exists && Node is null;

    /// <summary>The value's JS-ish type name: <c>undefined</c>, <c>null</c>, <c>string</c>,
    /// <c>number</c>, <c>boolean</c>, <c>array</c>, <c>object</c>.</summary>
    public string TypeName => !Exists ? "undefined" : Node switch
    {
        null => "null",
        JsonArray => "array",
        JsonObject => "object",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => "string",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => "number",
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "string",
    };

    /// <summary>The string a variable receives: raw text for strings, compact JSON for
    /// everything else, empty for undefined, <c>null</c> for JSON null.</summary>
    public string ToVariableString()
    {
        if (!Exists) return string.Empty;
        if (Node is null) return "null";
        if (Node is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        return Node.ToJsonString();
    }

    /// <summary>Short human display for previews and failure messages: strings are quoted,
    /// long values are truncated.</summary>
    public string ToDisplayString(int max = 120)
    {
        if (!Exists) return "undefined";
        if (Node is null) return "null";
        var s = Node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? "\"" + v.GetValue<string>() + "\""
            : Node.ToJsonString();
        return s.Length <= max ? s : s[..max] + "…";
    }

    public bool TryGetNumber(out double number)
    {
        number = 0;
        if (!Exists || Node is not JsonValue v) return false;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                number = AsDouble(v);
                return true;
            case JsonValueKind.String:
                return double.TryParse(v.GetValue<string>(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number);
            default:
                return false;
        }
    }

    /// <summary>Numeric value of a number node regardless of its backing CLR type (a node built
    /// from a <c>long</c> refuses <c>GetValue&lt;double&gt;()</c>).</summary>
    private static double AsDouble(JsonValue v) =>
        v.TryGetValue<double>(out var d) ? d
        : double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public bool TryGetString(out string s)
    {
        s = string.Empty;
        if (!Exists || Node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
        s = v.GetValue<string>();
        return true;
    }

    /// <summary>JS truthiness.</summary>
    public bool IsTruthy
    {
        get
        {
            if (!Exists || Node is null) return false;
            if (Node is JsonArray or JsonObject) return true;
            var v = (JsonValue)Node;
            return v.GetValueKind() switch
            {
                JsonValueKind.False => false,
                JsonValueKind.True => true,
                JsonValueKind.String => v.GetValue<string>().Length > 0,
                JsonValueKind.Number => AsDouble(v) is var d && d != 0 && !double.IsNaN(d),
                _ => false,
            };
        }
    }
}
