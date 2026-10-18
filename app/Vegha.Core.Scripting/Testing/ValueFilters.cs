using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vegha.Core.Scripting.Testing;

/// <summary>Raised when a filter can't be applied (unknown name, bad arguments, wrong input type).</summary>
public sealed class FilterException(string message) : Exception(message);

/// <summary>
/// Thunder Client–style value filters, shared by scriptless captures / assertions
/// (<c>res.body.items | first | prop("id")</c>) and <c>{{var | filter}}</c> interpolation.
/// Filters are pure: they take a <see cref="ResolvedValue"/> and return a new one.
/// </summary>
public static class ValueFilters
{
    /// <summary>Marker filters that change how a capture is applied rather than the value.</summary>
    public const string SetIfValue = "setIfValue";
    public const string SetNull = "setNull";

    /// <summary>Catalog for autocomplete: name, signature, one-line description.</summary>
    public static readonly IReadOnlyList<(string Name, string Signature, string Description)> Catalog = new[]
    {
        ("first", "first", "First element of an array"),
        ("last", "last", "Last element of an array"),
        ("at", "at(index)", "Element at index (negative counts from the end)"),
        ("prop", "prop(\"name\")", "Property of an object (of the first element for arrays)"),
        ("filter", "filter(key=value)", "Array elements matching a condition (= != > >= < <= *= ^= $=)"),
        ("map", "map(\"key\", …)", "Values of one key, or objects with only the given keys"),
        ("unique", "unique", "Array without duplicates"),
        ("count", "count", "Number of elements / characters / keys"),
        ("length", "length", "Number of elements / characters / keys"),
        ("keys", "keys", "Object keys as an array"),
        ("join", "join(\",\")", "Join array elements into a string"),
        ("split", "split(\",\")", "Split a string into an array"),
        ("slice", "slice(start, end)", "Sub-array or substring"),
        ("substring", "substring(start, end)", "Part of a string (negative start counts from the end)"),
        ("upper", "upper", "Uppercase"),
        ("lower", "lower", "Lowercase"),
        ("trim", "trim", "Strip surrounding whitespace"),
        ("replace", "replace(\"old\", \"new\")", "Replace every occurrence"),
        ("removeQuotes", "removeQuotes", "Strip quote characters"),
        ("removeSpaces", "removeSpaces", "Strip whitespace"),
        ("format", "format(\"Hello {0}\")", "Insert the value into a template"),
        ("default", "default(\"value\")", "Fallback when the value is missing, null or empty"),
        ("btoa", "btoa", "Base64-encode"),
        ("atob", "atob", "Base64-decode"),
        ("urlEncode", "urlEncode", "Percent-encode"),
        ("urlDecode", "urlDecode", "Percent-decode"),
        ("hash", "hash(\"SHA256\", \"hex\")", "Hash (MD5 / SHA1 / SHA256 / SHA512; base64 or hex)"),
        ("hmac", "hmac(\"secretVar\", \"SHA256\", \"hex\")", "HMAC keyed by a variable's value"),
        ("parseJSON", "parseJSON", "Parse a JSON string"),
        ("stringify", "stringify", "Serialize to a JSON string"),
        ("add", "add(n)", "Add a number (or a variable's value when quoted)"),
        ("subtract", "subtract(n)", "Subtract a number"),
        ("multiply", "multiply(n)", "Multiply by a number"),
        ("abs", "abs", "Absolute value"),
        ("isEmail", "isEmail", "true when the value looks like an email address"),
        ("isUrl", "isUrl", "true when the value is an absolute http(s) URL"),
        (SetIfValue, SetIfValue, "Capture only: skip when the value is missing or empty"),
        (SetNull, SetNull, "Capture only: store null instead of skipping when the value is null"),
    };

    private static readonly HashSet<string> Known =
        new(Catalog.Select(c => c.Name), StringComparer.Ordinal);

    public static bool IsKnown(string name) => Known.Contains(name);

    /// <summary>Applies a pipeline. <paramref name="lookupVar"/> resolves variable names for
    /// quoted numeric args (<c>add("count")</c>) and <c>hmac("secretVar")</c>.</summary>
    public static ResolvedValue ApplyAll(ResolvedValue value, IEnumerable<FilterCall> filters,
        Func<string, string?>? lookupVar = null)
    {
        foreach (var f in filters) value = Apply(value, f, lookupVar);
        return value;
    }

    public static ResolvedValue Apply(ResolvedValue value, FilterCall call, Func<string, string?>? lookupVar = null)
    {
        var a = call.Args;
        string Arg(int i, string fallback = "") => i < a.Count ? a[i].Value : fallback;

        switch (call.Name)
        {
            case SetIfValue:
            case SetNull:
                return value;

            case "first": return Index(value, 0, call.Name);
            case "last": return Index(value, -1, call.Name);
            case "at": return Index(value, ParseInt(Arg(0), call.Name), call.Name);

            case "prop":
            {
                var key = Arg(0);
                var target = value.Node is JsonArray arr ? (arr.Count > 0 ? arr[0] : null) : value.Node;
                return target is JsonObject obj && obj.TryGetPropertyValue(key, out var v)
                    ? ResolvedValue.Of(v?.DeepClone())
                    : ResolvedValue.Missing;
            }

            case "filter":
            {
                var arr = RequireArray(value, call.Name);
                var cond = FilterCondition.Parse(string.Join(",", a.Select(x => x.Value)));
                var result = new JsonArray();
                foreach (var item in arr)
                    if (cond.Matches(item)) result.Add(item?.DeepClone());
                return ResolvedValue.Of(result);
            }

            case "map":
            {
                var arr = RequireArray(value, call.Name);
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    if (a.Count == 1)
                    {
                        result.Add(item is JsonObject o && o.TryGetPropertyValue(Arg(0), out var v) ? v?.DeepClone() : null);
                        continue;
                    }
                    var subset = new JsonObject();
                    if (item is JsonObject src)
                        foreach (var k in a)
                            if (src.TryGetPropertyValue(k.Value, out var v)) subset[k.Value] = v?.DeepClone();
                    result.Add(subset);
                }
                return ResolvedValue.Of(result);
            }

            case "unique":
            {
                var arr = RequireArray(value, call.Name);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var result = new JsonArray();
                foreach (var item in arr)
                    if (seen.Add(item?.ToJsonString() ?? "null")) result.Add(item?.DeepClone());
                return ResolvedValue.Of(result);
            }

            case "count":
            case "length":
                return ResolvedValue.Of((long)Length(value));

            case "keys":
                return value.Node is JsonObject ko
                    ? ResolvedValue.Of(new JsonArray(ko.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)).ToArray()))
                    : throw new FilterException("keys: value is not an object");

            case "join":
            {
                var arr = RequireArray(value, call.Name);
                var sep = a.Count > 0 ? Arg(0) : ",";
                return ResolvedValue.Of(string.Join(sep, arr.Select(n => ResolvedValue.Of(n).ToVariableString())));
            }

            case "split":
            {
                var parts = Str(value).Split(Arg(0, ","));
                return ResolvedValue.Of(new JsonArray(parts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()));
            }

            case "slice":
            {
                if (value.Node is JsonArray arr)
                {
                    var (s, e) = Range(arr.Count, Arg(0, "0"), a.Count > 1 ? Arg(1) : null, call.Name);
                    return ResolvedValue.Of(new JsonArray(arr.Skip(s).Take(e - s).Select(n => n?.DeepClone()).ToArray()));
                }
                var str = Str(value);
                var (ss, se) = Range(str.Length, Arg(0, "0"), a.Count > 1 ? Arg(1) : null, call.Name);
                return ResolvedValue.Of(str[ss..se]);
            }

            case "substring":
            {
                var str = Str(value);
                var (s, e) = Range(str.Length, Arg(0, "0"), a.Count > 1 ? Arg(1) : null, call.Name);
                return ResolvedValue.Of(str[s..e]);
            }

            case "upper": return ResolvedValue.Of(Str(value).ToUpperInvariant());
            case "lower": return ResolvedValue.Of(Str(value).ToLowerInvariant());
            case "trim": return ResolvedValue.Of(Str(value).Trim());
            case "replace": return ResolvedValue.Of(Str(value).Replace(Arg(0), Arg(1), StringComparison.Ordinal));
            case "removeQuotes": return ResolvedValue.Of(Str(value).Replace("\"", "").Replace("'", ""));
            case "removeSpaces": return ResolvedValue.Of(Regex.Replace(Str(value), @"\s+", ""));
            case "format": return ResolvedValue.Of(Arg(0, "{0}").Replace("{0}", Str(value), StringComparison.Ordinal));

            case "default":
                return !value.Exists || value.IsNull || (value.TryGetString(out var ds) && ds.Length == 0)
                    ? ResolvedValue.Of(Arg(0))
                    : value;

            case "btoa": return ResolvedValue.Of(Convert.ToBase64String(Encoding.UTF8.GetBytes(Str(value))));
            case "atob":
                try { return ResolvedValue.Of(Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(Str(value))))); }
                catch (FormatException) { throw new FilterException("atob: value is not valid base64"); }
            case "urlEncode": return ResolvedValue.Of(Uri.EscapeDataString(Str(value)));
            case "urlDecode": return ResolvedValue.Of(Uri.UnescapeDataString(Str(value)));

            case "hash":
                return ResolvedValue.Of(Encode(HashBytes(Arg(0, "MD5"), Encoding.UTF8.GetBytes(Str(value))), Arg(1, "base64")));

            case "hmac":
            {
                var secret = lookupVar?.Invoke(Arg(0)) ?? throw new FilterException($"hmac: variable '{Arg(0)}' is not set");
                var key = Encoding.UTF8.GetBytes(secret);
                var data = Encoding.UTF8.GetBytes(Str(value));
                byte[] mac = Arg(1, "SHA256").ToUpperInvariant() switch
                {
                    "SHA1" => HMACSHA1.HashData(key, data),
                    "SHA256" => HMACSHA256.HashData(key, data),
                    "SHA512" => HMACSHA512.HashData(key, data),
                    "MD5" => HMACMD5.HashData(key, data),
                    var alg => throw new FilterException($"hmac: unsupported algorithm '{alg}'"),
                };
                return ResolvedValue.Of(Encode(mac, Arg(2, "base64")));
            }

            case "parseJSON":
                try { return ResolvedValue.Of(JsonNode.Parse(Str(value))); }
                catch (JsonException) { throw new FilterException("parseJSON: value is not valid JSON"); }
            case "stringify":
                return ResolvedValue.Of(value.Node?.ToJsonString() ?? "null");

            case "add": return Arithmetic(value, call, lookupVar, (x, y) => x + y);
            case "subtract": return Arithmetic(value, call, lookupVar, (x, y) => x - y);
            case "multiply": return Arithmetic(value, call, lookupVar, (x, y) => x * y);
            case "abs": return ResolvedValue.Of(Math.Abs(Num(value, call.Name)));

            case "isEmail": return ResolvedValue.Of(IsEmail(value.ToVariableString()));
            case "isUrl": return ResolvedValue.Of(IsUrl(value.ToVariableString()));

            default:
                throw new FilterException($"Unknown filter '{call.Name}'");
        }
    }

    public static bool IsEmail(string s) =>
        Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    public static bool IsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // ------------------------------------------------------------------ helpers

    private static ResolvedValue Index(ResolvedValue value, int index, string name)
    {
        var arr = RequireArray(value, name);
        if (index < 0) index += arr.Count;
        return index >= 0 && index < arr.Count ? ResolvedValue.Of(arr[index]?.DeepClone()) : ResolvedValue.Missing;
    }

    private static JsonArray RequireArray(ResolvedValue value, string name) =>
        value.Node as JsonArray ?? throw new FilterException($"{name}: value is {value.TypeName}, not an array");

    internal static int Length(ResolvedValue value) => value.Node switch
    {
        JsonArray arr => arr.Count,
        JsonObject obj => obj.Count,
        null => 0,
        _ => value.ToVariableString().Length,
    };

    private static string Str(ResolvedValue value) => value.ToVariableString();

    private static double Num(ResolvedValue value, string name) =>
        value.TryGetNumber(out var n) ? n : throw new FilterException($"{name}: value {value.ToDisplayString()} is not a number");

    private static ResolvedValue Arithmetic(ResolvedValue value, FilterCall call,
        Func<string, string?>? lookupVar, Func<double, double, double> op)
    {
        if (call.Args.Count == 0) throw new FilterException($"{call.Name}: missing argument");
        var arg = call.Args[0];
        var raw = arg.Quoted ? lookupVar?.Invoke(arg.Value) ?? throw new FilterException($"{call.Name}: variable '{arg.Value}' is not set") : arg.Value;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
            throw new FilterException($"{call.Name}: '{raw}' is not a number");
        var result = op(Num(value, call.Name), operand);
        return result == Math.Floor(result) && Math.Abs(result) < 1e15
            ? ResolvedValue.Of((long)result)
            : ResolvedValue.Of(result);
    }

    private static int ParseInt(string s, string name) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new FilterException($"{name}: '{s}' is not an integer");

    private static (int Start, int End) Range(int length, string start, string? end, string name)
    {
        var s = ParseInt(start, name);
        if (s < 0) s = Math.Max(0, length + s);
        s = Math.Min(s, length);
        var e = end is null ? length : ParseInt(end, name);
        if (e < 0) e = Math.Max(0, length + e);
        e = Math.Clamp(e, s, length);
        return (s, e);
    }

    private static string PadBase64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
    }

    private static byte[] HashBytes(string alg, byte[] data) => alg.ToUpperInvariant() switch
    {
        "MD5" => MD5.HashData(data),
        "SHA1" => SHA1.HashData(data),
        "SHA256" => SHA256.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw new FilterException($"hash: unsupported algorithm '{alg}'"),
    };

    private static string Encode(byte[] bytes, string encoding) => encoding.ToLowerInvariant() switch
    {
        "hex" => Convert.ToHexString(bytes).ToLowerInvariant(),
        "base64" => Convert.ToBase64String(bytes),
        _ => throw new FilterException($"unsupported encoding '{encoding}' (use hex or base64)"),
    };
}

/// <summary>A <c>key op value</c> condition used by the <c>filter()</c> filter and by chain-link
/// "run when" conditions. Operators (Thunder Client parity): <c>= != &gt; &gt;= &lt; &lt;=</c>,
/// <c>*=</c> contains, <c>^=</c> starts with, <c>$=</c> ends with.</summary>
public sealed record FilterCondition(string Key, string Operator, string Value)
{
    private static readonly string[] Operators = { ">=", "<=", "!=", "*=", "^=", "$=", "=", ">", "<" };

    public static FilterCondition Parse(string text)
    {
        foreach (var op in Operators)
        {
            var idx = IndexOfOperator(text, op);
            if (idx < 0) continue;
            var key = text[..idx].Trim();
            var value = text[(idx + op.Length)..].Trim();
            if (ResponseExpression.TryUnquote(value, out var unq)) value = unq;
            return new FilterCondition(key, op, value);
        }
        throw new FilterException($"filter: '{text}' has no operator (= != > >= < <= *= ^= $=)");
    }

    /// <summary>First occurrence of <paramref name="op"/> that isn't part of a longer operator
    /// (so <c>=</c> doesn't match inside <c>&gt;=</c>).</summary>
    private static int IndexOfOperator(string text, string op)
    {
        for (var i = text.IndexOf(op, StringComparison.Ordinal); i >= 0; i = text.IndexOf(op, i + 1, StringComparison.Ordinal))
        {
            if (op.Length == 1 && i > 0 && "<>!*^$".Contains(text[i - 1])) continue;
            if (op.Length == 1 && op != "=" && i + 1 < text.Length && text[i + 1] == '=') continue;
            return i;
        }
        return -1;
    }

    /// <summary>Evaluates against an array element: <see cref="Key"/> is a friendly path into
    /// the element (empty = the element itself).</summary>
    public bool Matches(JsonNode? element)
    {
        var actual = ResolvedValue.Of(element);
        if (Key.Length > 0)
        {
            if (!FriendlyPath.TryParse(Key, out var segs)) return false;
            actual = ResponseValueResolver.Navigate(actual, segs);
        }
        return Compare(actual, Operator, Value);
    }

    /// <summary>Compares a value with an operand string (numbers numerically when both parse).</summary>
    public static bool Compare(ResolvedValue actual, string op, string operand)
    {
        if (!actual.Exists) return op == "!=";
        var text = actual.ToVariableString();
        var bothNumbers = actual.TryGetNumber(out var a)
                          & double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);
        return op switch
        {
            "=" => bothNumbers ? a == b : string.Equals(text, operand, StringComparison.Ordinal),
            "!=" => bothNumbers ? a != b : !string.Equals(text, operand, StringComparison.Ordinal),
            ">" => bothNumbers ? a > b : string.CompareOrdinal(text, operand) > 0,
            ">=" => bothNumbers ? a >= b : string.CompareOrdinal(text, operand) >= 0,
            "<" => bothNumbers ? a < b : string.CompareOrdinal(text, operand) < 0,
            "<=" => bothNumbers ? a <= b : string.CompareOrdinal(text, operand) <= 0,
            "*=" => text.Contains(operand, StringComparison.Ordinal),
            "^=" => text.StartsWith(operand, StringComparison.Ordinal),
            "$=" => text.EndsWith(operand, StringComparison.Ordinal),
            _ => false,
        };
    }
}
