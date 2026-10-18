using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vegha.Core.Scripting.Testing;

/// <summary>One assertion operator: the Bruno token stored in the <c>assert { }</c> block, a
/// human label for the dropdown, and whether it takes an expected value.</summary>
public sealed record AssertionOperator(string Token, string Label, bool Unary, string? Hint = null);

/// <summary>Operator catalog — Bruno's <c>assert</c> operators plus Vegha extras
/// (<c>isObject</c>, <c>isEmail</c>, <c>isUrl</c>, <c>matchesSchema</c>).</summary>
public static class AssertionOperators
{
    public static readonly IReadOnlyList<AssertionOperator> All = new AssertionOperator[]
    {
        new("eq", "equals", false),
        new("neq", "not equals", false),
        new("gt", ">", false),
        new("gte", "≥", false),
        new("lt", "<", false),
        new("lte", "≤", false),
        new("contains", "contains", false),
        new("notContains", "not contains", false),
        new("startsWith", "starts with", false),
        new("endsWith", "ends with", false),
        new("matches", "matches regex", false, "^[a-f0-9-]{36}$"),
        new("notMatches", "not matches regex", false),
        new("in", "is one of", false, "a, b, c"),
        new("notIn", "is not one of", false, "a, b, c"),
        new("between", "between", false, "1, 10"),
        new("length", "length is", false, "3"),
        new("isDefined", "exists", true),
        new("isUndefined", "does not exist", true),
        new("isEmpty", "is empty", true),
        new("isNotEmpty", "is not empty", true),
        new("isNull", "is null", true),
        new("isNotNull", "is not null", true),
        new("isTruthy", "is truthy", true),
        new("isFalsy", "is falsy", true),
        new("isNumber", "is a number", true),
        new("isString", "is a string", true),
        new("isBoolean", "is a boolean", true),
        new("isArray", "is an array", true),
        new("isObject", "is an object", true),
        new("isJson", "is JSON", true),
        new("isEmail", "is an email", true),
        new("isUrl", "is a URL", true),
        new("matchesSchema", "matches JSON Schema", false, "{\"type\":\"object\"}"),
    };

    private static readonly Dictionary<string, AssertionOperator> ByToken =
        All.ToDictionary(o => o.Token, StringComparer.Ordinal);

    public static AssertionOperator? Find(string token) => ByToken.GetValueOrDefault(token);
}

/// <summary>The right-hand side of an <c>assert</c> entry: <c>eq 200</c>, <c>isNumber</c>,
/// <c>in "a", "b"</c>. A value without a known operator token means <c>eq &lt;value&gt;</c>
/// (Bruno behavior).</summary>
public sealed record AssertionSpec(string Operator, string Operand)
{
    public static AssertionSpec Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var space = text.IndexOfAny(new[] { ' ', '\t' });
        var token = space < 0 ? text : text[..space];
        if (AssertionOperators.Find(token) is not null)
            return new AssertionSpec(token, space < 0 ? string.Empty : text[(space + 1)..].Trim());
        return new AssertionSpec("eq", text);
    }

    public string ToStorage()
    {
        var op = AssertionOperators.Find(Operator);
        return op is { Unary: true } || Operand.Length == 0 ? Operator : $"{Operator} {Operand}";
    }

    public override string ToString() => ToStorage();
}

/// <summary>Evaluates scriptless assertions against a response.</summary>
public static class AssertionEvaluator
{
    /// <summary>Outcome of one assertion. <see cref="Actual"/> is what the left-hand side resolved to.</summary>
    public sealed record Outcome(string Name, bool Passed, string? FailureMessage, ResolvedValue Actual, double DurationMs)
    {
        public TestOutcome ToTestOutcome() => new(Name, Passed, FailureMessage, DurationMs);
    }

    /// <summary>Evaluates one stored assertion (<paramref name="expression"/> = the key,
    /// <paramref name="spec"/> = the value of an <c>assert { }</c> entry). The operand is
    /// <c>{{var}}</c>-interpolated through <paramref name="interpolate"/> first.</summary>
    public static Outcome Evaluate(
        string expression,
        string spec,
        ResponseSnapshot response,
        Func<string, string>? interpolate = null,
        Func<string, string?>? lookupVar = null,
        Func<string, ResponseSnapshot, ResolvedValue>? scriptEvaluator = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parsedSpec = AssertionSpec.Parse(spec);
        var name = $"{expression.Trim()} {parsedSpec.ToStorage()}".Trim();

        var expr = ResponseExpression.Parse(interpolate is null ? expression : interpolate(expression));
        var resolved = ResponseValueResolver.Resolve(expr, response, lookupVar, scriptEvaluator);
        if (resolved.IsError)
            return new Outcome(name, false, resolved.Error, ResolvedValue.Missing, sw.Elapsed.TotalMilliseconds);

        var operand = interpolate is null ? parsedSpec.Operand : interpolate(parsedSpec.Operand);
        var (passed, message) = Check(resolved.Value, parsedSpec.Operator, operand);
        return new Outcome(name, passed, passed ? null : message, resolved.Value, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Applies <paramref name="op"/> to <paramref name="actual"/>. Returns pass/fail
    /// plus a failure message phrased as "expected X to … but got Y".</summary>
    public static (bool Passed, string Message) Check(ResolvedValue actual, string op, string operand)
    {
        var got = actual.ToDisplayString();
        var label = AssertionOperators.Find(op)?.Label ?? op;
        string Fail(string expectation) => $"expected {expectation}, got {got}";

        switch (op)
        {
            case "eq":
                return (LooseEquals(actual, operand), Fail(operand));
            case "neq":
                return (!LooseEquals(actual, operand), $"expected a value other than {operand}");

            case "gt" or "gte" or "lt" or "lte":
            {
                if (!actual.TryGetNumber(out var a))
                    return (false, Fail($"a number {label} {operand}"));
                if (!TryNumber(operand, out var b))
                    return (false, $"expected value '{operand}' is not a number");
                var ok = op switch { "gt" => a > b, "gte" => a >= b, "lt" => a < b, _ => a <= b };
                return (ok, Fail($"{label} {operand}"));
            }

            case "contains":
                return (Contains(actual, operand), Fail($"something containing {operand}"));
            case "notContains":
                return (!Contains(actual, operand), Fail($"something not containing {operand}"));
            case "startsWith":
                return (actual.Exists && actual.ToVariableString().StartsWith(Unquote(operand), StringComparison.Ordinal),
                    Fail($"a value starting with {operand}"));
            case "endsWith":
                return (actual.Exists && actual.ToVariableString().EndsWith(Unquote(operand), StringComparison.Ordinal),
                    Fail($"a value ending with {operand}"));

            case "matches" or "notMatches":
            {
                var pattern = StripRegexSlashes(Unquote(operand));
                bool match;
                try { match = actual.Exists && Regex.IsMatch(actual.ToVariableString(), pattern, RegexOptions.None, TimeSpan.FromSeconds(2)); }
                catch (ArgumentException ex) { return (false, $"invalid regex /{pattern}/: {ex.Message}"); }
                return op == "matches"
                    ? (match, Fail($"a value matching /{pattern}/"))
                    : (!match, Fail($"a value not matching /{pattern}/"));
            }

            case "in" or "notIn":
            {
                var options = SplitList(operand);
                var found = options.Any(o => LooseEquals(actual, o));
                return op == "in"
                    ? (found, Fail($"one of [{string.Join(", ", options)}]"))
                    : (!found, Fail($"none of [{string.Join(", ", options)}]"));
            }

            case "between":
            {
                var bounds = SplitList(operand);
                if (bounds.Count != 2 || !TryNumber(bounds[0], out var lo) || !TryNumber(bounds[1], out var hi))
                    return (false, $"between needs two numbers, e.g. 'between 1, 10' (got '{operand}')");
                if (!actual.TryGetNumber(out var v)) return (false, Fail($"a number between {lo} and {hi}"));
                return (v >= lo && v <= hi, Fail($"a number between {lo} and {hi}"));
            }

            case "length":
            {
                if (!TryNumber(operand, out var n)) return (false, $"length needs a number (got '{operand}')");
                var len = actual.Exists ? ValueFilters.Length(actual) : -1;
                return (len == (int)n, $"expected length {operand}, got {(len < 0 ? "undefined" : len.ToString(CultureInfo.InvariantCulture))}");
            }

            case "isDefined": return (actual.Exists, "expected the value to exist, but it is undefined");
            case "isUndefined": return (!actual.Exists, Fail("undefined"));
            case "isEmpty": return (IsEmpty(actual), Fail("an empty value"));
            case "isNotEmpty": return (!IsEmpty(actual), Fail("a non-empty value"));
            case "isNull": return (actual.IsNull, Fail("null"));
            case "isNotNull": return (actual.Exists && !actual.IsNull, Fail("a non-null value"));
            case "isTruthy": return (actual.IsTruthy, Fail("a truthy value"));
            case "isFalsy": return (!actual.IsTruthy, Fail("a falsy value"));
            case "isNumber": return (actual.TypeName == "number", Fail("a number"));
            case "isString": return (actual.TypeName == "string", Fail("a string"));
            case "isBoolean": return (actual.TypeName == "boolean", Fail("a boolean"));
            case "isArray": return (actual.TypeName == "array", Fail("an array"));
            case "isObject": return (actual.TypeName == "object", Fail("an object"));
            case "isJson": return (IsJson(actual), Fail("a JSON object or array"));
            case "isEmail": return (actual.Exists && ValueFilters.IsEmail(actual.ToVariableString()), Fail("an email address"));
            case "isUrl": return (actual.Exists && ValueFilters.IsUrl(actual.ToVariableString()), Fail("an http(s) URL"));

            case "matchesSchema":
            {
                global::Json.Schema.JsonSchema schema;
                try { schema = global::Json.Schema.JsonSchema.FromText(operand); }
                catch (Exception ex) { return (false, "invalid JSON Schema: " + ex.Message); }
                if (!actual.Exists) return (false, "expected a value matching the schema, got undefined");
                var result = schema.Evaluate(actual.Node, new global::Json.Schema.EvaluationOptions
                {
                    OutputFormat = global::Json.Schema.OutputFormat.List,
                });
                if (result.IsValid) return (true, string.Empty);
                var errors = (result.Details ?? Array.Empty<global::Json.Schema.EvaluationResults>())
                    .Where(d => d.Errors is { Count: > 0 })
                    .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                    .Take(3);
                return (false, "schema mismatch — " + string.Join("; ", errors));
            }

            default:
                return (false, $"unknown operator '{op}'");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Lenient equality: numbers compare numerically (so <c>200</c> equals <c>"200"</c>),
    /// quoted operands compare as strings, JSON operands compare structurally.</summary>
    public static bool LooseEquals(ResolvedValue actual, string operand)
    {
        operand = operand.Trim();
        if (ResponseExpression.TryUnquote(operand, out var str))
            return actual.Exists && actual.ToVariableString() == str;
        if (operand == "undefined") return !actual.Exists;
        if (!actual.Exists) return false;
        if (operand == "null") return actual.IsNull;
        if (actual.Node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return string.Equals(operand, v.GetValueKind() == JsonValueKind.True ? "true" : "false", StringComparison.OrdinalIgnoreCase);
        if (actual.TryGetNumber(out var a) && TryNumber(operand, out var b))
            return a == b;
        if (actual.Node is JsonArray or JsonObject)
        {
            try { return JsonNode.DeepEquals(actual.Node, JsonNode.Parse(operand)); }
            catch (JsonException) { return false; }
        }
        return actual.ToVariableString() == operand;
    }

    private static bool Contains(ResolvedValue actual, string operand)
    {
        if (!actual.Exists || actual.IsNull) return false;
        return actual.Node switch
        {
            JsonArray arr => arr.Any(n => LooseEquals(ResolvedValue.Of(n), operand)),
            JsonObject obj => obj.ContainsKey(Unquote(operand)),
            _ => actual.ToVariableString().Contains(Unquote(operand), StringComparison.Ordinal),
        };
    }

    private static bool IsEmpty(ResolvedValue v) => !v.Exists || v.Node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        _ => v.ToVariableString().Length == 0,
    };

    private static bool IsJson(ResolvedValue v)
    {
        if (v.Node is JsonArray or JsonObject) return true;
        if (!v.TryGetString(out var s)) return false;
        s = s.Trim();
        if (s.Length == 0 || (s[0] != '{' && s[0] != '[')) return false;
        try { JsonNode.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    private static bool TryNumber(string s, out double n) =>
        double.TryParse(Unquote(s.Trim()), NumberStyles.Float, CultureInfo.InvariantCulture, out n);

    private static string Unquote(string s) =>
        ResponseExpression.TryUnquote(s.Trim(), out var u) ? u : s.Trim();

    private static string StripRegexSlashes(string pattern) =>
        pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/' ? pattern[1..^1] : pattern;

    /// <summary><c>[1, 2]</c>, <c>1, 2</c> or <c>"a", "b"</c> → items (quotes kept so
    /// <see cref="LooseEquals"/> still sees string intent).</summary>
    private static List<string> SplitList(string operand)
    {
        var text = operand.Trim();
        if (text.StartsWith('[') && text.EndsWith(']')) text = text[1..^1];
        return ResponseExpression.SplitTopLevel(text, ',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
