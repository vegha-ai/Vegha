namespace Vegha.Core.Scripting.Testing;

/// <summary>Where a captured value is written.</summary>
public enum CaptureScope
{
    /// <summary>Session-only runtime variable (Bruno's <c>bru.setVar</c>) — never written to disk.</summary>
    Runtime,
    /// <summary>The active environment (persisted).</summary>
    Environment,
    /// <summary>The active global environment (persisted).</summary>
    Global,
}

/// <summary>One scriptless "set variable from response" rule.</summary>
public sealed record CaptureRule(string Name, string Expression, CaptureScope Scope, bool Enabled = true);

/// <summary>Evaluates capture rules (the <c>vars:post-response[:env|:global]</c> blocks) against a
/// response and returns the values to write, bucketed by scope.</summary>
public static class CaptureEvaluator
{
    /// <summary>What happened to one rule. <see cref="Applied"/> is false when the rule was
    /// skipped (<c>| setIfValue</c> on an empty value) or failed (<see cref="Error"/> set).</summary>
    public sealed record Outcome(
        string Name,
        CaptureScope Scope,
        string Expression,
        ResolvedValue Value,
        bool Applied,
        string? Error);

    public sealed record Result(IReadOnlyList<Outcome> Outcomes)
    {
        public IReadOnlyDictionary<string, string> Runtime => Bucket(CaptureScope.Runtime);
        public IReadOnlyDictionary<string, string> Environment => Bucket(CaptureScope.Environment);
        public IReadOnlyDictionary<string, string> Global => Bucket(CaptureScope.Global);

        public bool HasErrors => Outcomes.Any(o => o.Error is not null);

        private IReadOnlyDictionary<string, string> Bucket(CaptureScope scope)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in Outcomes)
                if (o.Applied && o.Scope == scope) d[o.Name] = o.Value.ToVariableString();
            return d;
        }
    }

    public static readonly Result Empty = new(Array.Empty<Outcome>());

    /// <summary>Evaluates rules in order. Later rules see earlier captures through
    /// <paramref name="lookupVar"/> only if the caller layers them in; captures are otherwise
    /// independent. The expression is <c>{{var}}</c>-interpolated first (so
    /// <c>items | filter(id={{userId}})</c> works).</summary>
    public static Result Evaluate(
        IEnumerable<CaptureRule> rules,
        ResponseSnapshot response,
        Func<string, string>? interpolate = null,
        Func<string, string?>? lookupVar = null,
        Func<string, ResponseSnapshot, ResolvedValue>? scriptEvaluator = null)
    {
        var outcomes = new List<Outcome>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Name)) continue;
            outcomes.Add(EvaluateOne(rule, response, interpolate, lookupVar, scriptEvaluator));
        }
        return new Result(outcomes);
    }

    public static Outcome EvaluateOne(
        CaptureRule rule,
        ResponseSnapshot response,
        Func<string, string>? interpolate = null,
        Func<string, string?>? lookupVar = null,
        Func<string, ResponseSnapshot, ResolvedValue>? scriptEvaluator = null)
    {
        var raw = interpolate is null ? rule.Expression : interpolate(rule.Expression);
        var expr = ResponseExpression.Parse(raw);
        var resolved = ResponseValueResolver.Resolve(expr, response, lookupVar, scriptEvaluator);
        if (resolved.IsError)
            return new Outcome(rule.Name, rule.Scope, rule.Expression, ResolvedValue.Missing, false, resolved.Error);

        var value = resolved.Value;
        var setIfValue = expr.Filters.Any(f => f.Name == ValueFilters.SetIfValue);
        var setNull = expr.Filters.Any(f => f.Name == ValueFilters.SetNull);
        var empty = !value.Exists || value.IsNull || (value.TryGetString(out var s) && s.Length == 0);

        if (setNull && value.IsNull)
            return new Outcome(rule.Name, rule.Scope, rule.Expression, value, true, null);
        if (setIfValue && empty)
            return new Outcome(rule.Name, rule.Scope, rule.Expression, value, false, null);
        return new Outcome(rule.Name, rule.Scope, rule.Expression, value, true, null);
    }
}
