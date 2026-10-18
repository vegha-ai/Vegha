using Vegha.Core.Domain;
using Vegha.Core.Interpolation;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;

namespace Vegha.Core.Requests;

/// <summary>
/// The scriptless post-response steps shared by the request editor and the headless
/// <see cref="RequestPipeline"/>: captures (<c>vars:post-response[:env|:global]</c>) and
/// assertions (<c>assert { }</c>). Both evaluate natively; expressions outside the native
/// grammar fall back to <see cref="JintHost.EvaluateResponseExpression"/>.
/// </summary>
public static class ScriptlessSteps
{
    /// <summary>All capture rules of a request, runtime first, then environment, then global.</summary>
    public static IReadOnlyList<CaptureRule> CaptureRules(RequestItem request)
    {
        var rules = new List<CaptureRule>(
            request.PostResponseVars.Count + request.PostResponseEnvVars.Count + request.PostResponseGlobalVars.Count);
        foreach (var p in request.PostResponseVars) rules.Add(new CaptureRule(p.Name, p.Value, CaptureScope.Runtime, p.Enabled));
        foreach (var p in request.PostResponseEnvVars) rules.Add(new CaptureRule(p.Name, p.Value, CaptureScope.Environment, p.Enabled));
        foreach (var p in request.PostResponseGlobalVars) rules.Add(new CaptureRule(p.Name, p.Value, CaptureScope.Global, p.Enabled));
        return rules;
    }

    public static bool HasCaptures(RequestItem request) =>
        request.PostResponseVars.Any(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name))
        || request.PostResponseEnvVars.Any(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name))
        || request.PostResponseGlobalVars.Any(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name));

    public static ResponseSnapshot Snapshot(HttpExecutionResult result, string url) =>
        new(result.StatusCode, result.ReasonPhrase, result.Body, result.ElapsedMilliseconds, result.Headers, url);

    public static CaptureEvaluator.Result RunCaptures(
        IEnumerable<CaptureRule> rules,
        ResponseSnapshot response,
        IReadOnlyDictionary<string, string> vars,
        JintHost? scripting,
        CancellationToken ct = default) =>
        CaptureEvaluator.Evaluate(rules, response,
            interpolate: s => Interpolator.Resolve(s, vars),
            lookupVar: n => vars.TryGetValue(n, out var v) ? v : null,
            scriptEvaluator: ScriptEvaluator(scripting, vars, ct));

    public static IReadOnlyList<AssertionEvaluator.Outcome> RunAssertions(
        IEnumerable<KvPair> assertions,
        ResponseSnapshot response,
        IReadOnlyDictionary<string, string> vars,
        JintHost? scripting,
        CancellationToken ct = default)
    {
        var outcomes = new List<AssertionEvaluator.Outcome>();
        foreach (var a in assertions)
        {
            if (!a.Enabled || string.IsNullOrWhiteSpace(a.Name)) continue;
            outcomes.Add(AssertionEvaluator.Evaluate(a.Name, a.Value, response,
                interpolate: s => Interpolator.Resolve(s, vars),
                lookupVar: n => vars.TryGetValue(n, out var v) ? v : null,
                scriptEvaluator: ScriptEvaluator(scripting, vars, ct)));
        }
        return outcomes;
    }

    private static Func<string, ResponseSnapshot, ResolvedValue>? ScriptEvaluator(
        JintHost? scripting, IReadOnlyDictionary<string, string> vars, CancellationToken ct) =>
        scripting is null ? null : (expr, snap) => scripting.EvaluateResponseExpression(expr, snap, vars, ct);
}
