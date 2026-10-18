using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vegha.Core.Domain;
using Vegha.Core.Scripting.Testing;

namespace Vegha.Core.Importers;

/// <summary>
/// Lifts the simple, extremely common parts of a Postman test script into Vegha's scriptless
/// rows so imported collections are editable without JavaScript:
/// <list type="bullet">
/// <item><c>pm.environment.set("token", pm.response.json().access_token)</c> → an Environment
/// capture (<c>pm.variables</c> / <c>pm.collectionVariables</c> → Runtime, <c>pm.globals</c> → Global).</item>
/// <item><c>pm.test("…", () =&gt; { pm.response.to.have.status(200); })</c> and single
/// <c>pm.expect(&lt;response value&gt;).to.…</c> checks → assertions.</item>
/// <item>Vegha's own exported <c>pm.test("res.status eq 200", …)</c> lines → their original assertion.</item>
/// </list>
/// Anything else is left in the script untouched.
/// </summary>
public static class PostmanScriptlessLifter
{
    public sealed record Result(
        string? RemainingScript,
        List<KvPair> RuntimeCaptures,
        List<KvPair> EnvCaptures,
        List<KvPair> GlobalCaptures,
        List<KvPair> Assertions)
    {
        public bool LiftedAnything =>
            RuntimeCaptures.Count + EnvCaptures.Count + GlobalCaptures.Count + Assertions.Count > 0;
    }

    private static readonly Regex JsonAlias = new(
        @"^\s*(?:var|let|const)\s+([A-Za-z_$][\w$]*)\s*=\s*pm\.response\.json\(\)\s*;?\s*$",
        RegexOptions.Multiline);

    private static readonly Regex Setter = new(
        @"^[ \t]*pm\.(environment|globals|variables|collectionVariables)\.set\(\s*([""'])([^""'\n]+)\2\s*,\s*(.+?)\s*\)\s*;?[ \t]*$",
        RegexOptions.Multiline);

    private static readonly Regex TestBlock = new(
        @"^[ \t]*pm\.test\(\s*(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')\s*,\s*(?:function\s*\(\s*\)|\(\s*\)\s*=>)\s*\{\s*(?<body>[^{}]*?)\s*\}\s*\)\s*;?[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Singleline);

    public static Result Lift(string? script)
    {
        var empty = new Result(script, new(), new(), new(), new());
        if (string.IsNullOrWhiteSpace(script)) return empty;

        var aliases = JsonAlias.Matches(script).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var result = empty with { };
        var text = script;

        text = Setter.Replace(text, m =>
        {
            var expr = ToExpression(m.Groups[4].Value, aliases);
            if (expr is null) return m.Value;
            var pair = new KvPair(m.Groups[3].Value, expr);
            (m.Groups[1].Value switch
            {
                "environment" => result.EnvCaptures,
                "globals" => result.GlobalCaptures,
                _ => result.RuntimeCaptures,
            }).Add(pair);
            return string.Empty;
        });

        text = TestBlock.Replace(text, m =>
        {
            var assertion = FromTest(Unquote(m.Groups[1].Value), m.Groups["body"].Value.Trim(), aliases);
            if (assertion is null) return m.Value;
            result.Assertions.Add(assertion);
            return string.Empty;
        });

        // Drop now-unused `var jsonData = pm.response.json();` lines and collapse blank runs.
        foreach (var alias in aliases)
        {
            var uses = Regex.Matches(text, $@"\b{Regex.Escape(alias)}\b").Count;
            if (uses <= 1)
                text = Regex.Replace(text, $@"^\s*(?:var|let|const)\s+{Regex.Escape(alias)}\s*=\s*pm\.response\.json\(\)\s*;?\s*$\n?", string.Empty, RegexOptions.Multiline);
        }
        text = Regex.Replace(text, @"(\r?\n[ \t]*){3,}", "\n\n").Trim();
        return result with { RemainingScript = text.Length == 0 ? null : text };
    }

    // ------------------------------------------------------------------ assertions

    private static KvPair? FromTest(string name, string body, HashSet<string> aliases)
    {
        // Vegha's own export: pm.test("<expr> <spec>", function () { const actual = …; …; })
        if (body.StartsWith("const actual =", StringComparison.Ordinal) && SplitVeghaName(name) is { } own)
            return own;

        body = body.TrimEnd(';').Trim();
        if (body.Contains(';')) return null; // more than one statement — keep it as JS

        var status = Regex.Match(body, @"^pm\.response\.to\.have\.status\(\s*(\d{3})\s*\)$");
        if (status.Success) return new KvPair("res.status", "eq " + status.Groups[1].Value);

        var expect = Regex.Match(body, @"^pm\.expect\((?<actual>.+?)\)\.to\.(?<chain>[\w.]+)(?:\((?<arg>.*)\))?$");
        if (!expect.Success) return null;
        var actual = ToExpression(expect.Groups["actual"].Value, aliases);
        if (actual is null) return null;
        var arg = expect.Groups["arg"].Success ? expect.Groups["arg"].Value.Trim() : null;
        var spec = expect.Groups["chain"].Value switch
        {
            "eql" or "equal" or "eq" or "be.equal" or "deep.equal" when arg is not null && Literal(arg) is { } v => "eq " + v,
            "not.eql" or "not.equal" when arg is not null && Literal(arg) is { } v => "neq " + v,
            "be.above" or "be.greaterThan" when IsNumber(arg) => "gt " + arg,
            "be.at.least" when IsNumber(arg) => "gte " + arg,
            "be.below" or "be.lessThan" when IsNumber(arg) => "lt " + arg,
            "be.at.most" when IsNumber(arg) => "lte " + arg,
            "include" or "contain" when arg is not null && Literal(arg) is { } v => "contains " + v,
            "have.lengthOf" or "have.length" when IsNumber(arg) => "length " + arg,
            "be.a" or "be.an" when arg is not null => TypeCheck(Unquote(arg)),
            "be.true" => "eq true",
            "be.false" => "eq false",
            "be.null" => "isNull",
            "not.be.null" => "isNotNull",
            "be.undefined" => "isUndefined",
            "not.be.undefined" or "exist" => "isDefined",
            "be.empty" => "isEmpty",
            "not.be.empty" => "isNotEmpty",
            "be.ok" => "isTruthy",
            _ => null,
        };
        return spec is null ? null : new KvPair(actual, spec);
    }

    /// <summary>"res.status eq 200" → (res.status, eq 200); null when the name isn't in Vegha's form.</summary>
    private static KvPair? SplitVeghaName(string name)
    {
        // The spec is the operator token and everything after it; find the last operator token
        // that leaves a parseable expression on the left.
        var parts = name.Split(' ');
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            if (AssertionOperators.Find(parts[i]) is null) continue;
            var expr = string.Join(' ', parts[..i]);
            if (ResponseExpression.Parse(expr).Source == ResponseSource.Script) continue;
            return new KvPair(expr, string.Join(' ', parts[i..]));
        }
        return null;
    }

    private static string? TypeCheck(string type) => type switch
    {
        "number" => "isNumber",
        "string" => "isString",
        "boolean" => "isBoolean",
        "array" => "isArray",
        "object" => "isObject",
        _ => null,
    };

    // ------------------------------------------------------------------ values

    /// <summary>A pm.* response accessor → Vegha expression (<c>res.body.a[0]</c>, …), or null.</summary>
    public static string? ToExpression(string js, IReadOnlySet<string>? jsonAliases = null)
    {
        js = js.Trim();
        switch (js)
        {
            case "pm.response.code": return "res.status";
            case "pm.response.responseTime": return "res.responseTime";
            case "pm.response.text()": return "res.text";
        }
        var header = Regex.Match(js, @"^pm\.response\.headers\.get\(\s*([""'])([^""']+)\1\s*\)$");
        if (header.Success) return ResponseExpression.Create(ResponseSource.Header, header.Groups[2].Value).ToStorage();
        var cookie = Regex.Match(js, @"^pm\.cookies\.get\(\s*([""'])([^""']+)\1\s*\)$");
        if (cookie.Success) return ResponseExpression.Create(ResponseSource.Cookie, cookie.Groups[2].Value).ToStorage();

        string? rest = null;
        if (js.StartsWith("pm.response.json()", StringComparison.Ordinal)) rest = js["pm.response.json()".Length..];
        else if (jsonAliases is not null)
        {
            var m = Regex.Match(js, @"^([A-Za-z_$][\w$]*)(.*)$");
            if (m.Success && jsonAliases.Contains(m.Groups[1].Value)) rest = m.Groups[2].Value;
        }
        if (rest is null) return null;

        var path = ToFriendlyPath(rest);
        return path is null ? null : ResponseExpression.Create(ResponseSource.Body, path).ToStorage();
    }

    /// <summary><c>.a?.b[0]?.["x-y"]?.at(-1)</c> → <c>a.b[0]['x-y'][-1]</c>.</summary>
    private static string? ToFriendlyPath(string accessors)
    {
        var segs = new List<FriendlyPath.Segment>();
        // `at(n)` is tried before plain keys so `?.at(-1)` isn't read as a key named "at".
        var tokens = Regex.Matches(accessors,
            @"\G(?:\?\.|\.)?(?:at\(\s*(?<at>-?\d+)\s*\)|(?<key>[A-Za-z_$][\w$]*)|\[\s*(?<idx>-?\d+)\s*\]|\[\s*(?<q>[""'])(?<qkey>[^""']*)\k<q>\s*\])");
        var consumed = 0;
        foreach (Match t in tokens)
        {
            consumed += t.Length;
            if (t.Groups["at"].Success) segs.Add(new FriendlyPath.Segment(null, int.Parse(t.Groups["at"].Value, CultureInfo.InvariantCulture), false));
            else if (t.Groups["key"].Success) segs.Add(new FriendlyPath.Segment(t.Groups["key"].Value, null, false));
            else if (t.Groups["idx"].Success) segs.Add(new FriendlyPath.Segment(null, int.Parse(t.Groups["idx"].Value, CultureInfo.InvariantCulture), false));
            else if (t.Groups["qkey"].Success) segs.Add(new FriendlyPath.Segment(t.Groups["qkey"].Value, null, false));
        }
        // Anything left over (method calls, computed keys) isn't a plain path — keep it as JS.
        if (consumed != accessors.Length) return null;
        return FriendlyPath.Format(segs);
    }

    /// <summary>JS literal → assertion operand (numbers / booleans / null bare, strings quoted
    /// only when they'd otherwise read as another type).</summary>
    private static string? Literal(string js)
    {
        js = js.Trim();
        if (js is "true" or "false" or "null" || IsNumber(js)) return js;
        if (js.Length >= 2 && (js[0] == '"' || js[0] == '\'') && js[^1] == js[0])
        {
            var s = Unquote(js);
            return s.Length == 0 || s is "true" or "false" or "null" || IsNumber(s) || s.Trim() != s
                ? ResponseExpression.Quote(s, '"')
                : s;
        }
        return null;
    }

    private static bool IsNumber(string? s) =>
        s is not null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || (s[0] != '"' && s[0] != '\'') || s[^1] != s[0]) return s;
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length - 1) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
