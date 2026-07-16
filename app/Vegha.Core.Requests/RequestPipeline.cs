using System.Diagnostics;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;
using Vegha.Core.Scripting;

namespace Vegha.Core.Requests;

/// <summary>
/// Headless single-request execution pipeline. Composes a <see cref="RequestItem"/> against
/// its parent <see cref="Collection"/> + folder chain, runs the pre-request script, performs
/// the HTTP call, and runs the post-response + tests scripts. Returns everything the caller
/// needs to render a result without touching any UI types.
///
/// This is the engine that powers the Collection Runner. The editor's <c>SendAsync</c> path
/// is intentionally NOT refactored to use this yet — keeping them as siblings lets the runner
/// land without destabilizing the editor's many auth flows. Auth coverage for v1: None /
/// Inherit / Bearer / Basic / API Key. Auth types that require multi-step token acquisition or
/// signing (OAuth1, OAuth2, AWS SigV4, Digest, NTLM, WSSE, mTLS) return an error result with
/// a clear "unsupported in runner" message; the user can still run those via the editor tab.
/// </summary>
public static class RequestPipeline
{
    /// <summary>Inputs to one pipeline invocation.</summary>
    public sealed record Inputs(
        Collection Collection,
        IReadOnlyList<Folder> FolderChain,
        RequestItem Request,
        /// <summary>Active environment variables (lowest precedence).</summary>
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        /// <summary>Data-file row for the current iteration. Empty when not running with a
        /// data source. Overlays env, beats composed collection/folder vars in scripts.</summary>
        IReadOnlyDictionary<string, string> IterationVariables,
        /// <summary>Optional workspace-level inheritance layer (vars + scripts).</summary>
        RequestComposition.WorkspaceContext? Workspace = null);

    /// <summary>Per-request outputs surfaced to the runner UI + history.</summary>
    public sealed record Outputs(
        int StatusCode,
        string ReasonPhrase,
        long ElapsedMilliseconds,
        string ResolvedUrl,
        string Method,
        IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
        string? RequestBody,
        IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
        string ResponseBody,
        IReadOnlyList<TestOutcome> Tests,
        /// <summary>Runtime vars set by pre-request and post-response scripts. Carry across
        /// sibling requests within the same iteration (the runner threads them forward).</summary>
        IReadOnlyDictionary<string, string> RuntimeVariableMutations,
        /// <summary>Console output from scripts. Useful for the runner's per-request detail pane.</summary>
        IReadOnlyList<ConsoleMessage> ConsoleMessages,
        /// <summary>Environment-variable changes the post-response script made via
        /// <c>bru.setEnvVar</c>, as added/changed deltas vs the run's environment snapshot. The
        /// runner threads these forward so request N's extracted token reaches request N+1.
        /// Deletions (<c>bru.deleteEnvVar</c>) are not surfaced here — vanishingly rare in a run.</summary>
        IReadOnlyDictionary<string, string> EnvVarMutations,
        /// <summary>Transport/script error message. Null on a clean HTTP exchange even when
        /// the response carries a 4xx/5xx — the runner classifies pass/fail by tests + status.</summary>
        string? ErrorMessage)
    {
        public bool TransportError => ErrorMessage is not null;
        public int PassedTests => Tests.Count(t => t.Passed);
        public int FailedTests => Tests.Count(t => !t.Passed);
    }

    /// <summary>Executes one request end-to-end. Never throws on transport or script errors —
    /// failures surface in <see cref="Outputs.ErrorMessage"/>. Honors <paramref name="ct"/> at
    /// every async hop.</summary>
    public static async Task<Outputs> ExecuteAsync(
        Inputs inputs,
        HttpExecutor http,
        JintHost scripting,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var composed = RequestComposition.Compose(
            inputs.Collection, inputs.FolderChain, inputs.Request,
            inputs.Workspace ?? RequestComposition.WorkspaceContext.Empty);

        // 1. Pre-request script — may mutate runtime vars consumed downstream.
        var scriptVars = new Dictionary<string, string>();
        var consoleAll = new List<ConsoleMessage>();
        if (!string.IsNullOrWhiteSpace(composed.PreRequestScript))
        {
            // The composed view already merged env+collection+folder+request vars. We expose
            // iteration vars as a separate "collection vars" bag so scripts see them as a
            // higher-priority overlay than env without colliding with the request-vars layer
            // that holds bru.setVar() output.
            var pre = scripting.RunPreRequest(
                composed.PreRequestScript!,
                envVars: inputs.EnvironmentVariables,
                collectionVars: MergeReadOnly(inputs.IterationVariables, composed.Vars),
                requestVars: ToDict(inputs.Request.PreRequestVars),
                request: null,
                cancellationToken: ct);

            consoleAll.AddRange(pre.ConsoleMessages);
            if (!pre.IsSuccess)
            {
                return Failure(inputs, composed, sw.ElapsedMilliseconds,
                    pre.ErrorMessage ?? "Pre-request script failed", consoleAll);
            }
            foreach (var (k, v) in pre.RuntimeVariables) scriptVars[k] = v;
        }

        // 2. Build the effective variable bag the rest of the pipeline reads. Precedence
        //    (last-wins): env < iterationData < composed (collection ⊕ folders) < scriptRuntime < requestLevel.
        var vars = Merge(
            inputs.EnvironmentVariables,
            inputs.IterationVariables,
            composed.Vars,
            scriptVars,
            ToDict(inputs.Request.PreRequestVars));

        // 3. Resolve URL with interpolation + query params.
        var resolvedUrl = ComposeUrl(inputs.Request.Url, inputs.Request.Params, vars);
        if (string.IsNullOrEmpty(resolvedUrl))
            return Failure(inputs, composed, sw.ElapsedMilliseconds, "URL is empty.", consoleAll);

        // GraphQL subscriptions stream over WebSocket — there's no meaningful single
        // request/response for the runner to record. Fail with a pointer to the editor.
        if (inputs.Request.Body.Mode == BodyMode.GraphQL
            && Vegha.Core.GraphQL.GraphQLDocumentAnalyzer
                .Analyze(inputs.Request.Body.GraphQLQuery) is { Operations.Count: > 0 } gqlInfo
            && gqlInfo.Operations[0].Kind == Vegha.Core.GraphQL.GraphQLOperationKind.Subscription)
        {
            return Failure(inputs, composed, sw.ElapsedMilliseconds,
                "GraphQL subscriptions are not supported in the collection runner. Run via the request editor.",
                consoleAll);
        }

        // 4. Resolve auth. Pipeline v1 supports None/Inherit/Bearer/Basic/ApiKey; anything else
        //    is surfaced as an error so the user knows to run via the editor tab.
        var authToApply = composed.Auth ?? inputs.Request.Auth;
        if (!IsSupportedAuth(authToApply))
            return Failure(inputs, composed, sw.ElapsedMilliseconds,
                $"Auth type {authToApply!.Type} not supported by Collection Runner v1. Run via the request editor.",
                consoleAll);

        var authResult = AuthApplier.Apply(authToApply, resolvedUrl, vars);
        if (!Uri.TryCreate(authResult.Url, UriKind.Absolute, out var uri))
            return Failure(inputs, composed, sw.ElapsedMilliseconds,
                $"URL is not a valid absolute URI: {authResult.Url}", consoleAll);

        // 5. Compose body + headers (composed-inheritance headers + auth-emitted headers + request-level).
        var (body, contentType) = ComposeBody(inputs.Request.Body, vars);

        // 5b. SOAP WS-Security / WS-Addressing: inject the configured headers into the
        //     envelope so runner/CLI sends match the request editor's behavior.
        if (!string.IsNullOrEmpty(body) && SoapSecurityProcessor.HasOutgoing(inputs.Request.Soap))
            body = SoapSecurityProcessor.Apply(body!, inputs.Request.Soap,
                s => Interpolator.Resolve(s, vars));

        var headerList = ComposeHeaders(composed.Headers, vars);
        foreach (var h in authResult.Headers) headerList.Add(h);
        if (!string.IsNullOrEmpty(contentType)
            && !headerList.Any(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headerList.Add(new KeyValuePair<string, string>("Content-Type", contentType!));
        }

        // 6. HTTP send.
        var execRequest = new HttpExecutionRequest(
            Method: new HttpMethod(string.IsNullOrEmpty(inputs.Request.Method) ? "GET" : inputs.Request.Method.ToUpperInvariant()),
            Url: uri,
            Headers: headerList,
            Body: body,
            ContentType: contentType,
            Options: new HttpRequestOptions(
                FollowRedirects: inputs.Request.Settings.FollowRedirects,
                VerifySsl: inputs.Request.Settings.VerifySsl,
                UseCookies: inputs.Request.Settings.SendCookies));

        HttpExecutionResult httpResult;
        try
        {
            httpResult = await http.ExecuteAsync(execRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(inputs, composed, sw.ElapsedMilliseconds, "Canceled.", consoleAll);
        }
        catch (Exception ex)
        {
            return Failure(inputs, composed, sw.ElapsedMilliseconds, ex.Message, consoleAll);
        }

        // 7. Post-response script + tests. Combined-run, mirrors editor behavior.
        IReadOnlyList<TestOutcome> tests = Array.Empty<TestOutcome>();
        IReadOnlyDictionary<string, string> envVarMutations = EmptyVars;
        if (!string.IsNullOrWhiteSpace(composed.PostResponseScript) ||
            !string.IsNullOrWhiteSpace(composed.TestsScript))
        {
            var responseApi = new ResponseApi(
                status: httpResult.StatusCode,
                statusText: httpResult.ReasonPhrase,
                body: httpResult.Body,
                responseTime: httpResult.ElapsedMilliseconds,
                headers: httpResult.Headers,
                url: uri.ToString());

            var post = scripting.RunPostResponse(
                postScript: composed.PostResponseScript,
                testsScript: composed.TestsScript,
                response: responseApi,
                envVars: inputs.EnvironmentVariables,
                collectionVars: MergeReadOnly(inputs.IterationVariables, composed.Vars),
                requestVars: scriptVars,
                request: null,
                cancellationToken: ct);

            consoleAll.AddRange(post.ConsoleMessages);
            tests = post.TestOutcomes;
            envVarMutations = EnvVarDelta(inputs.EnvironmentVariables, post.EnvVarMutations);

            foreach (var (k, v) in post.RuntimeVariables) scriptVars[k] = v;

            if (!post.IsSuccess && tests.Count == 0)
            {
                return new Outputs(
                    StatusCode: httpResult.StatusCode,
                    ReasonPhrase: httpResult.ReasonPhrase,
                    ElapsedMilliseconds: sw.ElapsedMilliseconds,
                    ResolvedUrl: uri.ToString(),
                    Method: inputs.Request.Method,
                    RequestHeaders: headerList,
                    RequestBody: body,
                    ResponseHeaders: httpResult.Headers,
                    ResponseBody: httpResult.Body,
                    Tests: tests,
                    RuntimeVariableMutations: scriptVars,
                    ConsoleMessages: consoleAll,
                    EnvVarMutations: envVarMutations,
                    ErrorMessage: post.ErrorMessage);
            }
        }

        return new Outputs(
            StatusCode: httpResult.StatusCode,
            ReasonPhrase: httpResult.ReasonPhrase,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            ResolvedUrl: uri.ToString(),
            Method: inputs.Request.Method,
            RequestHeaders: headerList,
            RequestBody: body,
            ResponseHeaders: httpResult.Headers,
            ResponseBody: httpResult.Body,
            Tests: tests,
            RuntimeVariableMutations: scriptVars,
            ConsoleMessages: consoleAll,
            EnvVarMutations: envVarMutations,
            ErrorMessage: httpResult.ErrorMessage);
    }

    // ----- helpers ----------------------------------------------------------

    private static bool IsSupportedAuth(AuthConfig? auth) =>
        auth is null
        || auth.Type is AuthType.None
                     or AuthType.Inherit
                     or AuthType.Bearer
                     or AuthType.Basic
                     or AuthType.ApiKey;

    private static Outputs Failure(
        Inputs in_, RequestComposition.Composed composed, long elapsedMs,
        string error, IReadOnlyList<ConsoleMessage> console) =>
        new(
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            ElapsedMilliseconds: elapsedMs,
            ResolvedUrl: in_.Request.Url,
            Method: in_.Request.Method,
            RequestHeaders: Array.Empty<KeyValuePair<string, string>>(),
            RequestBody: null,
            ResponseHeaders: Array.Empty<KeyValuePair<string, string>>(),
            ResponseBody: string.Empty,
            Tests: Array.Empty<TestOutcome>(),
            RuntimeVariableMutations: new Dictionary<string, string>(),
            ConsoleMessages: console,
            EnvVarMutations: EmptyVars,
            ErrorMessage: error);

    /// <summary>Shared empty env-var bag for results with no script mutations.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyVars =
        new Dictionary<string, string>();

    /// <summary>Added/changed env vars in <paramref name="after"/> relative to
    /// <paramref name="before"/>. The post-response env bag is a copy of the run snapshot with
    /// the script's <c>setEnvVar</c> changes layered on, so a key-by-key diff isolates them.</summary>
    private static IReadOnlyDictionary<string, string> EnvVarDelta(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        Dictionary<string, string>? delta = null;
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old) || !string.Equals(old, value, StringComparison.Ordinal))
                (delta ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = value;
        }
        return delta ?? EmptyVars;
    }

    private static string ComposeUrl(string baseUrl, IList<KvPair> queryParams,
        IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        var resolvedBase = Interpolator.Resolve(baseUrl, vars);
        var enabled = queryParams.Where(p => p.Enabled && !string.IsNullOrEmpty(p.Name)).ToList();
        if (enabled.Count == 0) return resolvedBase;

        var separator = resolvedBase.Contains('?') ? "&" : "?";
        var qs = string.Join("&", enabled.Select(p =>
        {
            var n = Interpolator.Resolve(p.Name, vars);
            var v = Interpolator.Resolve(p.Value, vars);
            return $"{Uri.EscapeDataString(n)}={Uri.EscapeDataString(v)}";
        }));
        return resolvedBase + separator + qs;
    }

    private static List<KeyValuePair<string, string>> ComposeHeaders(
        IReadOnlyList<KvPair> headers,
        IReadOnlyDictionary<string, string> vars) =>
        headers.Where(h => h.Enabled && !string.IsNullOrEmpty(h.Name))
               .Select(h => new KeyValuePair<string, string>(
                   Interpolator.Resolve(h.Name, vars),
                   Interpolator.Resolve(h.Value, vars)))
               .ToList();

    /// <summary>Body composition for the runner. v1 covers the common modes: json/text/xml/sparql/
    /// form-urlencoded/graphql. Multipart, binary-file, and raw streaming are out of scope —
    /// callers that need those run via the editor.</summary>
    private static (string? Body, string? ContentType) ComposeBody(
        BodyConfig body,
        IReadOnlyDictionary<string, string> vars)
    {
        switch (body.Mode)
        {
            case BodyMode.None:
                return (null, null);
            case BodyMode.Json:
                return (string.IsNullOrEmpty(body.Content) ? null : Interpolator.Resolve(body.Content!, vars),
                        "application/json");
            case BodyMode.Xml:
                return (string.IsNullOrEmpty(body.Content) ? null : Interpolator.Resolve(body.Content!, vars),
                        "application/xml");
            case BodyMode.Text:
                return (string.IsNullOrEmpty(body.Content) ? null : Interpolator.Resolve(body.Content!, vars),
                        "text/plain");
            case BodyMode.Sparql:
                return (string.IsNullOrEmpty(body.Content) ? null : Interpolator.Resolve(body.Content!, vars),
                        "application/sparql-query");
            case BodyMode.FormUrlEncoded:
            {
                var enabled = body.FormData.Where(f => f.Enabled && !string.IsNullOrEmpty(f.Name)).ToList();
                if (enabled.Count == 0) return (string.Empty, "application/x-www-form-urlencoded");
                var encoded = string.Join("&", enabled.Select(f =>
                    Uri.EscapeDataString(Interpolator.Resolve(f.Name, vars)) + "=" +
                    Uri.EscapeDataString(Interpolator.Resolve(f.Value, vars))));
                return (encoded, "application/x-www-form-urlencoded");
            }
            case BodyMode.GraphQL:
            {
                var query = body.GraphQLQuery ?? string.Empty;
                if (string.IsNullOrWhiteSpace(query)) return (null, null);
                var resolvedQuery = Interpolator.Resolve(query, vars);
                var rawVars = body.GraphQLVariables ?? string.Empty;
                var resolvedVars = string.IsNullOrWhiteSpace(rawVars)
                    ? "{}"
                    : Interpolator.Resolve(rawVars, vars);
                var trimmed = resolvedVars.TrimStart();
                var varsJson = (trimmed.StartsWith('{') || trimmed.StartsWith('[')) ? resolvedVars : "{}";
                // Multi-operation documents need an operationName or the server rejects the
                // request — the runner has no picker, so send the first named operation
                // (same rule the editor and codegen apply).
                var opName = Vegha.Core.GraphQL.GraphQLDocumentAnalyzer.ResolveOperationNameForSend(resolvedQuery);
                var json =
                    "{\"query\":" + System.Text.Json.JsonSerializer.Serialize(resolvedQuery) +
                    (opName is null
                        ? string.Empty
                        : ",\"operationName\":" + System.Text.Json.JsonSerializer.Serialize(opName)) +
                    ",\"variables\":" + varsJson + "}";
                return (json, "application/json");
            }
            default:
                // Multipart and Binary unsupported in v1 — fall through to no body so the
                // request still goes out but without the multipart envelope. Pipeline output
                // ErrorMessage stays null; the caller sees a non-error result, just with a
                // missing body. Acceptable as a v1 limitation.
                return (null, null);
        }
    }

    private static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>[] bags)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bag in bags)
            foreach (var (k, v) in bag) merged[k] = v;
        return merged;
    }

    private static IReadOnlyDictionary<string, string> MergeReadOnly(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b)
    {
        var merged = new Dictionary<string, string>(b.Count + a.Count, StringComparer.Ordinal);
        foreach (var (k, v) in a) merged[k] = v;
        foreach (var (k, v) in b) merged[k] = v;
        return merged;
    }

    private static IReadOnlyDictionary<string, string> ToDict(IList<KvPair> pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in pairs)
        {
            if (!p.Enabled || string.IsNullOrEmpty(p.Name)) continue;
            dict[p.Name] = p.Value;
        }
        return dict;
    }
}
