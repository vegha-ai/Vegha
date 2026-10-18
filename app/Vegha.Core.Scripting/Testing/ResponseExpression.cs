using System.Globalization;
using System.Text;

namespace Vegha.Core.Scripting.Testing;

/// <summary>Where a scriptless capture / assertion reads its value from.</summary>
public enum ResponseSource
{
    /// <summary>Parsed JSON body, navigated with a friendly path (<c>data.items[0].id</c>).</summary>
    Body,
    /// <summary>Parsed JSON body queried with an RFC 9535 JSONPath (<c>$..id</c>).</summary>
    JsonPath,
    Header,
    Cookie,
    Status,
    ResponseTime,
    /// <summary>The raw body text.</summary>
    BodyText,
    XPath,
    /// <summary>A regular expression run against the raw body; <see cref="ResponseExpression.RegexGroup"/> picks the group.</summary>
    Regex,
    /// <summary>A fixed value (quoted string, number, boolean or null).</summary>
    Literal,
    /// <summary>Anything the native grammar can't read — evaluated as JavaScript against <c>res</c> / <c>bru</c>.</summary>
    Script,
}

/// <summary>One <c>| name(args)</c> step of a filter pipeline.</summary>
public sealed record FilterCall(string Name, IReadOnlyList<FilterArg> Args)
{
    public override string ToString() =>
        Args.Count == 0 ? Name : $"{Name}({string.Join(", ", Args.Select(a => a.ToString()))})";
}

/// <summary>A filter argument. <see cref="Quoted"/> distinguishes <c>add("count")</c> (a variable
/// name in Thunder Client semantics) from <c>add(5)</c>, and keeps round-trips faithful.</summary>
public sealed record FilterArg(string Value, bool Quoted)
{
    public override string ToString() => Quoted ? ResponseExpression.Quote(Value, '"') : Value;
}

/// <summary>
/// The stored form of a scriptless capture / assertion left-hand side, e.g.
/// <c>res.body.data.token</c>, <c>res.headers['x-request-id']</c>,
/// <c>res.body.items | filter(active=true) | first | prop("id")</c>.
///
/// The grammar is a strict subset of the JavaScript Bruno evaluates for <c>vars:post-response</c>
/// and <c>assert</c> keys, so plain Bruno files keep working; filters (<c>| name(args)</c>, Thunder
/// Client style) and the <c>jsonpath()</c> / <c>xpath()</c> / <c>regex()</c> / <c>res.cookies</c>
/// heads are Vegha extensions. Anything that doesn't match is kept verbatim as
/// <see cref="ResponseSource.Script"/> and evaluated by Jint.
/// </summary>
public sealed record ResponseExpression(
    ResponseSource Source,
    string Path,
    IReadOnlyList<FilterCall> Filters,
    int RegexGroup = 1)
{
    private static readonly IReadOnlyList<FilterCall> NoFilters = Array.Empty<FilterCall>();

    public static ResponseExpression Create(ResponseSource source, string? path = null,
        IReadOnlyList<FilterCall>? filters = null, int regexGroup = 1) =>
        new(source, path ?? string.Empty, filters ?? NoFilters, regexGroup);

    /// <summary>Parses a stored expression. Never throws — unrecognized input becomes
    /// <see cref="ResponseSource.Script"/>.</summary>
    public static ResponseExpression Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return Create(ResponseSource.Body);

        // `||` is JS logical-or; don't mistake it for a filter pipeline.
        var segments = text.Contains("||", StringComparison.Ordinal)
            ? new List<string> { text }
            : SplitTopLevel(text, '|');

        var filters = new List<FilterCall>();
        for (var i = 1; i < segments.Count; i++)
        {
            if (!TryParseCall(segments[i].Trim(), out var name, out var args, allowBare: true))
                return Create(ResponseSource.Script, text);
            filters.Add(new FilterCall(name, args));
        }

        var head = ParseHead(segments[0].Trim());
        if (head.Source == ResponseSource.Script && filters.Count > 0 && segments.Count > 1)
        {
            // Unknown head with a pipeline — keep it all as script rather than half-parsing.
            return Create(ResponseSource.Script, text);
        }
        return head with { Filters = filters };
    }

    /// <summary>Renders the canonical stored form (the inverse of <see cref="Parse"/>).</summary>
    public string ToStorage()
    {
        var head = Source switch
        {
            ResponseSource.Body => Path.Length == 0 ? "res.body"
                : Path[0] == '[' ? "res.body" + Path : "res.body." + Path,
            ResponseSource.JsonPath => $"jsonpath({QuoteBest(Path)})",
            ResponseSource.Header => $"res.headers[{QuoteBest(Path)}]",
            ResponseSource.Cookie => $"res.cookies[{QuoteBest(Path)}]",
            ResponseSource.Status => "res.status",
            ResponseSource.ResponseTime => "res.responseTime",
            ResponseSource.BodyText => "res.text",
            ResponseSource.XPath => $"xpath({QuoteBest(Path)})",
            ResponseSource.Regex => RegexGroup == 1 ? $"regex({QuoteBest(Path)})" : $"regex({QuoteBest(Path)}, {RegexGroup})",
            ResponseSource.Literal => LiteralToStorage(Path),
            _ => Path,
        };
        if (Source == ResponseSource.Script || Filters.Count == 0) return head;
        return head + " | " + string.Join(" | ", Filters.Select(f => f.ToString()));
    }

    public override string ToString() => ToStorage();

    /// <summary>Builds an expression from the editor's Source dropdown + path box, where the
    /// path box holds the path followed by an optional <c>| filter</c> pipeline
    /// (<c>items | filter(active=true) | first</c>). Script and Literal text is taken verbatim.</summary>
    public static ResponseExpression FromEditor(ResponseSource source, string? pathText, int regexGroup = 1)
    {
        var text = (pathText ?? string.Empty).Trim();
        if (source is ResponseSource.Script or ResponseSource.Literal)
            return Create(source, text);

        var parts = text.Contains("||", StringComparison.Ordinal) ? new List<string> { text } : SplitTopLevel(text, '|');
        var filters = new List<FilterCall>();
        foreach (var f in parts.Skip(1))
        {
            if (TryParseCall(f.Trim(), out var name, out var args, allowBare: true)) filters.Add(new FilterCall(name, args));
        }
        var path = parts[0].Trim();
        if (source == ResponseSource.Body)
        {
            // Tolerate pasted prefixes: "res.body.x", "$.x" (JSONPath), ".x".
            if (path.StartsWith("res.body", StringComparison.Ordinal)) path = path["res.body".Length..];
            if (path.StartsWith('$')) return Create(ResponseSource.JsonPath, path, filters);
            path = path.TrimStart('.');
        }
        return Create(source, path, filters, regexGroup);
    }

    /// <summary>The editor's path-box text: the path plus <c> | filter</c> steps.</summary>
    public string ToEditorText() =>
        Source is ResponseSource.Script or ResponseSource.Literal || Filters.Count == 0
            ? Path
            : (Path.Length == 0 ? "" : Path + " ") + "| " + string.Join(" | ", Filters.Select(f => f.ToString()));

    // ------------------------------------------------------------------ heads

    private static ResponseExpression ParseHead(string head)
    {
        switch (head)
        {
            case "res.status":
            case "res.getStatus()":
                return Create(ResponseSource.Status);
            case "res.responseTime":
            case "res.getResponseTime()":
                return Create(ResponseSource.ResponseTime);
            case "res.text":
            case "res.getBodyAsText()":
                return Create(ResponseSource.BodyText);
        }

        if (TryStripPrefix(head, "res.body", out var bodyRest) || TryStripPrefix(head, "res.getBody()", out bodyRest))
        {
            if (bodyRest.Length == 0) return Create(ResponseSource.Body);
            if ((bodyRest[0] == '.' || bodyRest[0] == '[') && IsFriendlyPath(bodyRest))
                return Create(ResponseSource.Body, bodyRest[0] == '.' ? bodyRest[1..] : bodyRest);
            return Create(ResponseSource.Script, head);
        }

        if (TryParseIndexer(head, "res.headers", out var header)) return Create(ResponseSource.Header, header);
        if (TryParseIndexer(head, "res.cookies", out var cookie)) return Create(ResponseSource.Cookie, cookie);

        if (TryParseCall(head, out var fn, out var args, allowBare: false))
        {
            switch (fn)
            {
                case "res.getHeader" when args.Count == 1:
                    return Create(ResponseSource.Header, args[0].Value);
                case "jsonpath" when args.Count == 1:
                    return Create(ResponseSource.JsonPath, args[0].Value);
                case "xpath" when args.Count == 1:
                    return Create(ResponseSource.XPath, args[0].Value);
                case "regex" when args.Count is 1 or 2:
                    var group = 1;
                    if (args.Count == 2 && !int.TryParse(args[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out group))
                        break;
                    return Create(ResponseSource.Regex, args[0].Value, regexGroup: group);
            }
        }

        if (TryParseLiteral(head, out var literal)) return Create(ResponseSource.Literal, literal);
        return Create(ResponseSource.Script, head);
    }

    private static bool TryStripPrefix(string s, string prefix, out string rest)
    {
        rest = string.Empty;
        if (!s.StartsWith(prefix, StringComparison.Ordinal)) return false;
        rest = s[prefix.Length..];
        // `res.bodyX` is a different identifier.
        if (rest.Length > 0 && rest[0] != '.' && rest[0] != '[') return false;
        return true;
    }

    /// <summary><c>res.headers['x']</c>, <c>res.headers["x"]</c> or <c>res.headers.x</c>.</summary>
    private static bool TryParseIndexer(string s, string prefix, out string key)
    {
        key = string.Empty;
        if (!s.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = s[prefix.Length..];
        if (rest.Length > 1 && rest[0] == '.' && rest[1..].All(IsIdentChar))
        {
            key = rest[1..];
            return true;
        }
        if (rest.Length > 3 && rest[0] == '[' && rest[^1] == ']'
            && TryUnquote(rest[1..^1].Trim(), out key))
            return true;
        return false;
    }

    /// <summary>True when <paramref name="path"/> (starting with '.' or '[') is a chain of
    /// <c>.key</c>, <c>[n]</c>, <c>[-n]</c>, <c>[*]</c> or <c>['key']</c> segments.</summary>
    public static bool IsFriendlyPath(string path) => FriendlyPath.TryParse(path, out _);

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '-';

    // ------------------------------------------------------------------ literals

    private static bool TryParseLiteral(string s, out string value)
    {
        value = string.Empty;
        if (TryUnquote(s, out var unq))
        {
            value = unq;
            return true;
        }
        if (s is "true" or "false" or "null"
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = s;
            return true;
        }
        return false;
    }

    private static string LiteralToStorage(string path)
    {
        if (path.Length == 0) return "''";
        if (path is "true" or "false" or "null"
            || double.TryParse(path, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return path;
        return Quote(path);
    }

    // ------------------------------------------------------------------ shared lexing

    /// <summary>Single-quotes <paramref name="s"/>, or double-quotes it when it contains single
    /// quotes but no double quotes (XPath predicates like <c>local-name()='Body'</c>) so the
    /// stored text stays readable.</summary>
    public static string QuoteBest(string s) =>
        s.Contains('\'') && !s.Contains('"') ? Quote(s, '"') : Quote(s);

    /// <summary>Quotes <paramref name="s"/> with <paramref name="quote"/>, escaping backslashes and the quote char.</summary>
    public static string Quote(string s, char quote = '\'')
    {
        // Only escape backslashes that would otherwise read as an escape (before a quote,
        // another backslash, or the closing quote) so regex text like \d stays readable.
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == quote) sb.Append('\\').Append(c);
            else if (c == '\\' && (i + 1 == s.Length || s[i + 1] == '\\' || s[i + 1] == quote)) sb.Append("\\\\");
            else sb.Append(c);
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Strips matching single/double quotes and unescapes <c>\'</c>, <c>\"</c>, <c>\\</c>.</summary>
    public static bool TryUnquote(string s, out string value)
    {
        value = string.Empty;
        if (s.Length < 2) return false;
        var q = s[0];
        if ((q != '\'' && q != '"') || s[^1] != q) return false;
        var sb = new StringBuilder(s.Length);
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length - 1)
            {
                var next = s[i + 1];
                if (next == q || next == '\\') { sb.Append(next); i++; continue; }
            }
            else if (c == q)
            {
                return false; // unescaped quote in the middle → not a single literal
            }
            sb.Append(c);
        }
        value = sb.ToString();
        return true;
    }

    /// <summary>Splits on <paramref name="separator"/> outside quotes / brackets / parens.</summary>
    public static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'' or '"' or '`': quote = c; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth = Math.Max(0, depth - 1); break;
                default:
                    if (c == separator && depth == 0)
                    {
                        parts.Add(s[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>Parses <c>name</c> (when <paramref name="allowBare"/>) or <c>name(arg, …)</c>.</summary>
    public static bool TryParseCall(string s, out string name, out IReadOnlyList<FilterArg> args, bool allowBare)
    {
        name = string.Empty;
        args = Array.Empty<FilterArg>();
        var open = s.IndexOf('(');
        if (open < 0)
        {
            if (!allowBare || s.Length == 0 || !s.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
            name = s;
            return true;
        }
        if (s[^1] != ')') return false;
        name = s[..open].Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')) return false;
        var inner = s[(open + 1)..^1];
        if (inner.Trim().Length == 0) return true;
        var list = new List<FilterArg>();
        foreach (var part in SplitTopLevel(inner, ','))
        {
            var p = part.Trim();
            list.Add(TryUnquote(p, out var unq) ? new FilterArg(unq, true) : new FilterArg(p, false));
        }
        args = list;
        return true;
    }
}

/// <summary>A parsed friendly path such as <c>data.items[0].id</c> or <c>['odd key'][-1]</c>.</summary>
public static class FriendlyPath
{
    /// <summary>One navigation step: an object key, an array index (negative counts from the
    /// end), or <c>[*]</c> (map over every element).</summary>
    public readonly record struct Segment(string? Key, int? Index, bool Wildcard);

    public static bool TryParse(string? path, out IReadOnlyList<Segment> segments)
    {
        var list = new List<Segment>();
        segments = list;
        if (string.IsNullOrEmpty(path)) return true;
        var i = 0;
        // A leading bare key (no '.') is allowed: `data.items`.
        if (path[0] != '.' && path[0] != '[')
            path = "." + path;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                var start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                var key = path[start..i];
                if (key.Length == 0 || key.Any(ch => char.IsWhiteSpace(ch) || ch is '(' or ')' or ']' or '\'' or '"'))
                    return false;
                list.Add(key == "*" ? new Segment(null, null, true) : new Segment(key, null, false));
            }
            else if (c == '[')
            {
                var close = FindClose(path, i);
                if (close < 0) return false;
                var inner = path[(i + 1)..close].Trim();
                if (inner == "*") list.Add(new Segment(null, null, true));
                else if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var idx))
                    list.Add(new Segment(null, idx, false));
                else if (ResponseExpression.TryUnquote(inner, out var key)) list.Add(new Segment(key, null, false));
                else return false;
                i = close + 1;
            }
            else return false;
        }
        return true;
    }

    /// <summary>Renders segments back to the friendly form (keys that aren't plain identifiers
    /// use bracket-quote notation).</summary>
    public static string Format(IEnumerable<Segment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (s.Wildcard) sb.Append("[*]");
            else if (s.Index is { } idx) sb.Append('[').Append(idx.ToString(CultureInfo.InvariantCulture)).Append(']');
            else if (IsPlainKey(s.Key!)) { if (sb.Length > 0) sb.Append('.'); sb.Append(s.Key); }
            else sb.Append('[').Append(ResponseExpression.Quote(s.Key!)).Append(']');
        }
        return sb.ToString();
    }

    public static bool IsPlainKey(string key) =>
        key.Length > 0 && !char.IsDigit(key[0]) && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

    private static int FindClose(string s, int open)
    {
        char quote = '\0';
        for (var i = open + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c == ']') return i;
        }
        return -1;
    }
}
