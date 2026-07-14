using Vegha.Core.Domain;
using Vegha.Core.Flow;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;

namespace Vegha.App.ViewModels.Runner;

/// <summary>Bridges <see cref="CollectionRunOrchestrator"/>'s executor delegate to the
/// <see cref="RequestPipeline.ExecuteAsync"/> headless pipeline. Threads runtime variables
/// across consecutive requests within the same iteration so <c>bru.setVar</c> output from
/// request N flows into request N+1.
///
/// One instance per run — the runtime-var dictionary is per-iteration to avoid cross-worker
/// state bleed. The pipeline itself is stateless and safe to invoke concurrently as long as
/// the HttpExecutor / JintHost dependencies are.</summary>
public sealed class PipelineRequestExecutor
{
    private readonly HttpExecutor _http;
    private readonly JintHost _scripting;
    private readonly RequestComposition.WorkspaceContext _workspace;
    private readonly Vegha.Integrations.Secrets.SecretRegistry? _secretRegistry;
    private readonly bool _persistResponses;
    private readonly bool _turnOffLogs;
    private readonly bool _keepVariableValues;

    /// <summary>Per-iteration accumulator of runtime vars set by <c>bru.setVar</c> in earlier
    /// requests of that same iteration. Keyed by iteration index so workers don't trample
    /// each other when <see cref="RunnerOptions.Workers"/> > 1.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<string, string>> _perIterationRuntimeVars = new();

    public PipelineRequestExecutor(
        HttpExecutor http,
        JintHost scripting,
        RequestComposition.WorkspaceContext? workspace = null,
        Vegha.Integrations.Secrets.SecretRegistry? secretRegistry = null,
        bool persistResponses = true,
        bool turnOffLogs = false,
        bool keepVariableValues = true)
    {
        _http = http;
        _scripting = scripting;
        _workspace = workspace ?? RequestComposition.WorkspaceContext.Empty;
        _secretRegistry = secretRegistry;
        _persistResponses = persistResponses;
        _turnOffLogs = turnOffLogs;
        _keepVariableValues = keepVariableValues;
    }

    public CollectionRunOrchestrator.RequestExecutor AsDelegate(
        Collection collection,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        // Pre-resolve secret://… environment values once for the whole run, against the
        // configured secret managers. A shared Task — every iteration awaits the same result.
        var resolvedEnv = _secretRegistry is null
            ? Task.FromResult(environmentVariables)
            : ResolveEnvironmentAsync(environmentVariables);

        return async (iterIndex, request, chain, iterVars, ct) =>
        {
            var env = await resolvedEnv.ConfigureAwait(false);
            var carriedVars = _perIterationRuntimeVars.GetOrAdd(iterIndex, _ => new Dictionary<string, string>());

            // Layer carried runtime vars on top of the iteration data row so the pipeline
            // sees both as the "iteration overlay" — scripts that read prior bru.setVar
            // output via {{token}} resolve correctly without leaking into env.
            IReadOnlyDictionary<string, string> overlayVars = MergeOverlay(iterVars, carriedVars);

            var inputs = new RequestPipeline.Inputs(
                Collection: collection,
                FolderChain: chain,
                Request: request,
                EnvironmentVariables: env,
                IterationVariables: overlayVars,
                Workspace: _workspace);

            var outputs = await RequestPipeline.ExecuteAsync(inputs, _http, _scripting, ct).ConfigureAwait(false);

            // Carry variable mutations forward only when "Keep variable values" is on. When off,
            // each request starts from the iteration's data row with no chained-in overlay.
            if (_keepVariableValues)
            {
                // Runtime-var mutations for the next request in this same iteration.
                foreach (var (k, v) in outputs.RuntimeVariableMutations)
                    carriedVars[k] = v;
                // bru.setEnvVar mutations carry forward the same way (as an iteration overlay) so a
                // token extracted in request N's post-response resolves via {{token}} in request N+1.
                // The overlay beats env at compose time, so the fresh value wins over the snapshot.
                foreach (var (k, v) in outputs.EnvVarMutations)
                    carriedVars[k] = v;
            }

            var status = ClassifyResult(outputs);
            var sizeBytes = ComputeSizeBytes(outputs);
            var detail = _persistResponses ? BuildDetail(outputs) : null;
            var folderPath = chain.Count == 0 ? string.Empty : string.Join(" / ", chain.Select(f => f.Name));

            return new RequestRunResult(
                Name: request.Name,
                Method: request.Method,
                Url: outputs.ResolvedUrl,
                StatusCode: outputs.StatusCode,
                ElapsedMs: outputs.ElapsedMilliseconds,
                Succeeded: status == RequestRunStatus.Passed,
                ErrorMessage: outputs.ErrorMessage,
                Status: status,
                PassedTests: outputs.PassedTests,
                FailedTests: outputs.FailedTests,
                Kind: request.Kind,
                ResponseSizeBytes: sizeBytes,
                Detail: detail,
                FolderPath: folderPath);
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        IReadOnlyDictionary<string, string> environmentVariables) =>
        await _secretRegistry!.ResolveSecretsAsync(environmentVariables).ConfigureAwait(false);

    /// <summary>Total response size = header bytes + body bytes, matching what Postman reports
    /// in the result row. Header size approximates "Name: value\r\n" lines.</summary>
    private static long ComputeSizeBytes(RequestPipeline.Outputs outputs)
    {
        long total = System.Text.Encoding.UTF8.GetByteCount(outputs.ResponseBody ?? string.Empty);
        foreach (var (k, v) in outputs.ResponseHeaders)
            total += System.Text.Encoding.UTF8.GetByteCount(k) + System.Text.Encoding.UTF8.GetByteCount(v ?? string.Empty) + 4;
        return total;
    }

    /// <summary>Maps pipeline outputs into the Flow-layer detail record for the results detail
    /// pane. Console lines are dropped when "Turn off logs during run" is set.</summary>
    private RunResultDetail BuildDetail(RequestPipeline.Outputs outputs) =>
        new(
            ResponseBody: outputs.ResponseBody ?? string.Empty,
            ResponseHeaders: outputs.ResponseHeaders,
            RequestBody: outputs.RequestBody,
            RequestHeaders: outputs.RequestHeaders,
            Tests: outputs.Tests
                .Select(t => new RunTestOutcome(t.Name, t.Passed, t.FailureMessage))
                .ToList(),
            Console: _turnOffLogs
                ? Array.Empty<RunConsoleMessage>()
                : outputs.ConsoleMessages.Select(m => new RunConsoleMessage(m.Level, m.Text)).ToList());

    private static RequestRunStatus ClassifyResult(RequestPipeline.Outputs outputs)
    {
        if (outputs.TransportError) return RequestRunStatus.Errored;
        if (outputs.FailedTests > 0) return RequestRunStatus.Failed;
        if (outputs.StatusCode is >= 200 and < 400) return RequestRunStatus.Passed;
        // 4xx/5xx without explicit failing tests: still treat as "Failed" (the response carried
        // an error status). Users can override by writing tests that explicitly accept the code.
        return RequestRunStatus.Failed;
    }

    private static IReadOnlyDictionary<string, string> MergeOverlay(
        IReadOnlyDictionary<string, string> iterVars,
        IReadOnlyDictionary<string, string> carriedRuntime)
    {
        if (carriedRuntime.Count == 0) return iterVars;
        var merged = new Dictionary<string, string>(iterVars.Count + carriedRuntime.Count, StringComparer.Ordinal);
        foreach (var (k, v) in iterVars) merged[k] = v;
        foreach (var (k, v) in carriedRuntime) merged[k] = v;  // runtime beats iter (script wins)
        return merged;
    }
}
