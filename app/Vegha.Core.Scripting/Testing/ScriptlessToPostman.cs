using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vegha.Core.Scripting.Testing;

/// <summary>
/// Translates scriptless captures and assertions into equivalent Postman <c>pm.*</c> script
/// lines for export. Expressions with no faithful JavaScript form (JSONPath / XPath / custom
/// JS heads, unsupported filters) come back in <see cref="Result.Untranslated"/> and as a
/// commented line, so nothing disappears silently.
/// </summary>
public static class ScriptlessToPostman
{
    public sealed record Result(IReadOnlyList<string> Lines, IReadOnlyList<string> Untranslated);

    /// <summary><c>pm.variables / pm.environment / pm.globals .set(...)</c> per capture.</summary>
    public static Result Captures(IEnumerable<CaptureRule> rules)
    {
        var lines = new List<string>();
        var skipped = new List<string>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Name)) continue;
            var expr = ResponseExpression.Parse(rule.Expression);
            var js = ToJs(expr, out var setIfValue);
            var target = rule.Scope switch
            {
                CaptureScope.Environment => "pm.environment",
                CaptureScope.Global => "pm.globals",
                _ => "pm.variables",
            };
            if (js is null)
            {
                skipped.Add($"{rule.Name} = {rule.Expression}");
                lines.Add($"// Vegha capture not translated: {rule.Name} = {rule.Expression}");
                continue;
            }
            var name = JsonSerializer.Serialize(rule.Name.Trim());
            lines.Add(setIfValue
                ? $"{{ const v = {js}; if (v !== undefined && v !== null && v !== \"\") {target}.set({name}, v); }}"
                : $"{target}.set({name}, {js});");
        }
        return new Result(lines, skipped);
    }

    /// <summary>One <c>pm.test(...)</c> per assertion (expression = the <c>assert</c> key,
    /// spec = its value such as <c>eq 200</c>).</summary>
    public static Result Assertions(IEnumerable<(string Expression, string Spec, bool Enabled)> assertions)
    {
        var lines = new List<string>();
        var skipped = new List<string>();
        foreach (var (expression, specText, enabled) in assertions)
        {
            if (!enabled || string.IsNullOrWhiteSpace(expression)) continue;
            var spec = AssertionSpec.Parse(specText);
            var name = $"{expression.Trim()} {spec.ToStorage()}";
            var actual = ToJs(ResponseExpression.Parse(expression), out _);
            var check = actual is null ? null : Check(spec.Operator, spec.Operand);
            if (check is null)
            {
                skipped.Add(name);
                lines.Add($"// Vegha assertion not translated: {name}");
                continue;
            }
            lines.Add($"pm.test({JsonSerializer.Serialize(name)}, function () {{ const actual = {actual}; {check}; }});");
        }
        return new Result(lines, skipped);
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>JavaScript reading the value in Postman, or null when there's no faithful form.</summary>
    public static string? ToJs(ResponseExpression expr, out bool setIfValue)
    {
        setIfValue = false;
        string? head = expr.Source switch
        {
            ResponseSource.Status => "pm.response.code",
            ResponseSource.ResponseTime => "pm.response.responseTime",
            ResponseSource.BodyText => "pm.response.text()",
            ResponseSource.Header => $"pm.response.headers.get({JsonSerializer.Serialize(expr.Path)})",
            ResponseSource.Cookie => $"pm.cookies.get({JsonSerializer.Serialize(expr.Path)})",
            ResponseSource.Literal => LiteralJs(expr.Path),
            ResponseSource.Regex => $"(pm.response.text().match(new RegExp({JsonSerializer.Serialize(expr.Path)})) || [])[{expr.RegexGroup}]",
            ResponseSource.Body => FriendlyPath.TryParse(expr.Path, out var segs) ? PathJs("pm.response.json()", segs) : null,
            _ => null, // JSONPath / XPath / JS expressions have no faithful pm.* equivalent
        };
        if (head is null) return null;
        foreach (var f in expr.Filters)
        {
            if (f.Name == ValueFilters.SetIfValue) { setIfValue = true; continue; }
            head = FilterJs(head, f);
            if (head is null) return null;
        }
        return head;
    }

    private static string? PathJs(string root, IReadOnlyList<FriendlyPath.Segment> segments)
    {
        var sb = new StringBuilder(root);
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Wildcard)
            {
                var rest = PathJs("e", segments.Skip(i + 1).ToList());
                return rest is null ? null : $"({sb})?.map(e => {rest})";
            }
            if (s.Index is { } idx) sb.Append(idx < 0 ? $"?.at({idx})" : $"?.[{idx}]");
            else if (FriendlyPath.IsPlainKey(s.Key!)) sb.Append("?.").Append(s.Key);
            else sb.Append("?.[").Append(JsonSerializer.Serialize(s.Key)).Append(']');
        }
        return sb.ToString();
    }

    private static string? FilterJs(string x, FilterCall f)
    {
        string Arg(int i, string fallback = "") => i < f.Args.Count ? f.Args[i].Value : fallback;
        return f.Name switch
        {
            "first" => $"({x})?.[0]",
            "last" => $"({x})?.at(-1)",
            "at" => int.TryParse(Arg(0), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? $"({x})?.at({n})" : null,
            "count" or "length" => $"({x})?.length",
            "upper" => $"String({x}).toUpperCase()",
            "lower" => $"String({x}).toLowerCase()",
            "trim" => $"String({x}).trim()",
            "btoa" => $"btoa(String({x}))",
            "atob" => $"atob(String({x}))",
            "urlEncode" => $"encodeURIComponent(String({x}))",
            "urlDecode" => $"decodeURIComponent(String({x}))",
            "join" => $"({x}).join({JsonSerializer.Serialize(f.Args.Count > 0 ? Arg(0) : ",")})",
            "split" => $"String({x}).split({JsonSerializer.Serialize(Arg(0, ","))})",
            "unique" => $"[...new Set({x})]",
            "default" => $"(({x}) ?? {JsonSerializer.Serialize(Arg(0))})",
            "keys" => $"Object.keys({x})",
            _ => null,
        };
    }

    private static string LiteralJs(string text)
    {
        var v = ResponseValueResolver.ParseLiteral(text);
        return v.Node?.ToJsonString() ?? "null";
    }

    // ------------------------------------------------------------------ operators

    /// <summary>The chai expectation for one operator, or null when it has no equivalent.</summary>
    private static string? Check(string op, string operand)
    {
        var e = "pm.expect(actual)";
        switch (op)
        {
            case "eq": return Equality(operand, negate: false);
            case "neq": return Equality(operand, negate: true);
            case "gt": return $"{e}.to.be.above({Num(operand)})";
            case "gte": return $"{e}.to.be.at.least({Num(operand)})";
            case "lt": return $"{e}.to.be.below({Num(operand)})";
            case "lte": return $"{e}.to.be.at.most({Num(operand)})";
            case "contains": return $"{e}.to.include({Value(operand)})";
            case "notContains": return $"{e}.to.not.include({Value(operand)})";
            case "startsWith": return $"pm.expect(String(actual).startsWith({Str(operand)})).to.be.true";
            case "endsWith": return $"pm.expect(String(actual).endsWith({Str(operand)})).to.be.true";
            case "matches": return $"pm.expect(String(actual)).to.match(new RegExp({Str(StripSlashes(operand))}))";
            case "notMatches": return $"pm.expect(String(actual)).to.not.match(new RegExp({Str(StripSlashes(operand))}))";
            case "in": return $"{e}.to.be.oneOf([{string.Join(", ", List(operand).Select(Value))}])";
            case "notIn": return $"{e}.to.not.be.oneOf([{string.Join(", ", List(operand).Select(Value))}])";
            case "between":
            {
                var b = List(operand);
                return b.Count == 2 ? $"{e}.to.be.within({Num(b[0])}, {Num(b[1])})" : null;
            }
            case "length": return $"{e}.to.have.lengthOf({Num(operand)})";
            case "isDefined": return $"{e}.to.not.be.undefined";
            case "isUndefined": return $"{e}.to.be.undefined";
            case "isEmpty": return $"{e}.to.be.empty";
            case "isNotEmpty": return $"{e}.to.not.be.empty";
            case "isNull": return $"{e}.to.be.null";
            case "isNotNull": return $"{e}.to.not.be.null";
            case "isTruthy": return $"{e}.to.be.ok";
            case "isFalsy": return $"{e}.to.not.be.ok";
            case "isNumber": return $"{e}.to.be.a('number')";
            case "isString": return $"{e}.to.be.a('string')";
            case "isBoolean": return $"{e}.to.be.a('boolean')";
            case "isArray": return $"{e}.to.be.an('array')";
            case "isObject": return $"{e}.to.be.an('object')";
            case "isJson": return $"pm.expect(typeof actual === 'object' && actual !== null).to.be.true";
            case "isEmail": return "pm.expect(String(actual)).to.match(/^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$/)";
            case "isUrl": return "pm.expect(String(actual)).to.match(/^https?:\\/\\//)";
            default: return null; // matchesSchema: Postman's schema helpers apply to the whole response only
        }
    }

    /// <summary>Vegha's lenient equality, spelled out per operand type.</summary>
    private static string Equality(string operand, bool negate)
    {
        var to = negate ? "to.not" : "to";
        var t = operand.Trim();
        if (ResponseExpression.TryUnquote(t, out var s)) return $"pm.expect(String(actual)).{to}.eql({JsonSerializer.Serialize(s)})";
        if (t.Contains("{{", StringComparison.Ordinal)) return $"pm.expect(String(actual)).{to}.eql({Str(t)})";
        if (t is "true" or "false") return $"pm.expect(actual).{to}.eql({t})";
        if (t == "null") return $"pm.expect(actual).{to}.be.null";
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return $"pm.expect(Number(actual)).{to}.eql({t})";
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try { return $"pm.expect(actual).{to}.eql({JsonNode.Parse(t)!.ToJsonString()})"; }
            catch (JsonException) { }
        }
        return $"pm.expect(String(actual)).{to}.eql({JsonSerializer.Serialize(t)})";
    }

    /// <summary>A JS string expression; <c>{{var}}</c> references resolve at run time.</summary>
    private static string Str(string text)
    {
        var t = ResponseExpression.TryUnquote(text.Trim(), out var u) ? u : text.Trim();
        var lit = JsonSerializer.Serialize(t);
        return t.Contains("{{", StringComparison.Ordinal) ? $"pm.variables.replaceIn({lit})" : lit;
    }

    private static string Num(string text)
    {
        var t = text.Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? t : $"Number({Str(t)})";
    }

    /// <summary>Typed JS literal for list items / contains operands.</summary>
    private static string Value(string text)
    {
        var t = text.Trim();
        if (ResponseExpression.TryUnquote(t, out var s)) return JsonSerializer.Serialize(s);
        if (t is "true" or "false" or "null" || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return t;
        return Str(t);
    }

    private static List<string> List(string operand)
    {
        var t = operand.Trim();
        if (t.StartsWith('[') && t.EndsWith(']')) t = t[1..^1];
        return ResponseExpression.SplitTopLevel(t, ',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    private static string StripSlashes(string p)
    {
        var t = p.Trim();
        return t.Length >= 2 && t[0] == '/' && t[^1] == '/' ? t[1..^1] : t;
    }
}
