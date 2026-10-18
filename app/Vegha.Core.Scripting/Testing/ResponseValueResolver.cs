using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Vegha.Core.Scripting.Testing;

/// <summary>Evaluates a <see cref="ResponseExpression"/> against a <see cref="ResponseSnapshot"/>.
/// Everything except <see cref="ResponseSource.Script"/> is evaluated natively (no JS engine);
/// script expressions go through the optional <c>scriptEvaluator</c> callback.</summary>
public static class ResponseValueResolver
{
    /// <summary>Result of a resolve: the value, or an error message when evaluation failed
    /// (bad JSONPath, filter misuse, script error…). A missing value is not an error.</summary>
    public readonly record struct Result(ResolvedValue Value, string? Error)
    {
        public bool IsError => Error is not null;
    }

    public static Result Resolve(
        ResponseExpression expr,
        ResponseSnapshot response,
        Func<string, string?>? lookupVar = null,
        Func<string, ResponseSnapshot, ResolvedValue>? scriptEvaluator = null)
    {
        try
        {
            var head = ResolveHead(expr, response, scriptEvaluator);
            return new Result(ValueFilters.ApplyAll(head, expr.Filters, lookupVar), null);
        }
        catch (FilterException fex)
        {
            return new Result(ResolvedValue.Missing, fex.Message);
        }
        catch (Exception ex) when (ex is RegexParseException or XPathException or ArgumentException or FormatException or InvalidOperationException)
        {
            return new Result(ResolvedValue.Missing, ex.Message);
        }
    }

    private static ResolvedValue ResolveHead(ResponseExpression expr, ResponseSnapshot r,
        Func<string, ResponseSnapshot, ResolvedValue>? scriptEvaluator)
    {
        switch (expr.Source)
        {
            case ResponseSource.Status:
                return ResolvedValue.Of((long)r.Status);
            case ResponseSource.ResponseTime:
                return ResolvedValue.Of(r.ElapsedMilliseconds);
            case ResponseSource.BodyText:
                return ResolvedValue.Of(r.Body);
            case ResponseSource.Header:
                return ResolvedValue.Of(r.GetHeader(expr.Path));
            case ResponseSource.Cookie:
                return r.Cookies.TryGetValue(expr.Path, out var cookie) ? ResolvedValue.Of(cookie) : ResolvedValue.Missing;

            case ResponseSource.Body:
            {
                if (!r.IsJson)
                {
                    // Non-JSON body: the whole-body path still yields the text (Bruno's res.body
                    // is the raw string for non-JSON responses); deeper paths are undefined.
                    return expr.Path.Length == 0 ? ResolvedValue.Of(r.Body) : ResolvedValue.Missing;
                }
                if (!FriendlyPath.TryParse(expr.Path, out var segments))
                    throw new FilterException($"Invalid path '{expr.Path}'");
                return Navigate(ResolvedValue.Of(r.Json), segments);
            }

            case ResponseSource.JsonPath:
            {
                if (!r.IsJson) return ResolvedValue.Missing;
                var path = global::Json.Path.JsonPath.Parse(expr.Path);
                var matches = path.Evaluate(r.Json).Matches;
                if (matches is null || matches.Count == 0) return ResolvedValue.Missing;
                // A single-match "singular" query returns the value itself; anything else an array.
                if (matches.Count == 1 && path.IsSingular) return ResolvedValue.Of(matches[0].Value?.DeepClone());
                return ResolvedValue.Of(new JsonArray(matches.Select(m => m.Value?.DeepClone()).ToArray()));
            }

            case ResponseSource.XPath:
            {
                var doc = r.Xml;
                if (doc is null) return ResolvedValue.Missing;
                return EvaluateXPath(doc, expr.Path);
            }

            case ResponseSource.Regex:
            {
                var m = System.Text.RegularExpressions.Regex.Match(r.Body, expr.Path, RegexOptions.None, TimeSpan.FromSeconds(2));
                if (!m.Success) return ResolvedValue.Missing;
                var g = expr.RegexGroup;
                if (g < 0 || g >= m.Groups.Count) g = m.Groups.Count > 1 ? 1 : 0;
                return ResolvedValue.Of(m.Groups[g].Value);
            }

            case ResponseSource.Literal:
                return ParseLiteral(expr.Path);

            case ResponseSource.Script:
                if (scriptEvaluator is null)
                    throw new FilterException("JavaScript expressions aren't supported here");
                return scriptEvaluator(expr.Path, r);

            default:
                return ResolvedValue.Missing;
        }
    }

    /// <summary>Walks friendly-path segments. <c>[*]</c> maps the rest of the path over every
    /// element and returns an array.</summary>
    public static ResolvedValue Navigate(ResolvedValue start, IReadOnlyList<FriendlyPath.Segment> segments)
    {
        var current = start;
        for (var i = 0; i < segments.Count; i++)
        {
            if (!current.Exists) return ResolvedValue.Missing;
            var seg = segments[i];
            if (seg.Wildcard)
            {
                var items = current.Node switch
                {
                    JsonArray arr => arr.ToList(),
                    JsonObject obj => obj.Select(kv => kv.Value).ToList(),
                    _ => null,
                };
                if (items is null) return ResolvedValue.Missing;
                var rest = segments.Skip(i + 1).ToList();
                var mapped = new JsonArray();
                foreach (var item in items)
                {
                    var v = Navigate(ResolvedValue.Of(item), rest);
                    if (v.Exists) mapped.Add(v.Node?.DeepClone());
                }
                return ResolvedValue.Of(mapped);
            }
            if (seg.Index is { } idx)
            {
                if (current.Node is not JsonArray arr) return ResolvedValue.Missing;
                if (idx < 0) idx += arr.Count;
                if (idx < 0 || idx >= arr.Count) return ResolvedValue.Missing;
                current = ResolvedValue.Of(arr[idx]);
                continue;
            }
            if (current.Node is JsonObject o && o.TryGetPropertyValue(seg.Key!, out var child))
            {
                current = ResolvedValue.Of(child);
                continue;
            }
            if (current.Node is JsonArray la && seg.Key == "length")
            {
                current = ResolvedValue.Of((long)la.Count);
                continue;
            }
            return ResolvedValue.Missing;
        }
        return current.Exists ? ResolvedValue.Of(current.Node?.DeepClone()) : current;
    }

    private static ResolvedValue EvaluateXPath(XDocument doc, string xpath)
    {
        var nav = doc.CreateNavigator();
        var result = nav.Evaluate(xpath);
        switch (result)
        {
            case double d: return ResolvedValue.Of(d);
            case bool b: return ResolvedValue.Of(b);
            case string s: return ResolvedValue.Of(s);
            case XPathNodeIterator it:
            {
                var values = new List<string>();
                while (it.MoveNext()) values.Add(it.Current?.Value ?? string.Empty);
                return values.Count switch
                {
                    0 => ResolvedValue.Missing,
                    1 => ResolvedValue.Of(values[0]),
                    _ => ResolvedValue.Of(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())),
                };
            }
            default: return ResolvedValue.Missing;
        }
    }

    /// <summary>Literal text → typed value: numbers, true/false/null, otherwise a string.</summary>
    public static ResolvedValue ParseLiteral(string text)
    {
        if (text == "null") return ResolvedValue.Null;
        if (text == "true") return ResolvedValue.Of(true);
        if (text == "false") return ResolvedValue.Of(false);
        if (long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var l))
            return ResolvedValue.Of(l);
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return ResolvedValue.Of(d);
        return ResolvedValue.Of(text);
    }
}
