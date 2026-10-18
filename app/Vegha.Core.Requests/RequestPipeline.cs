using System.Diagnostics;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;

namespace Vegha.Core.Requests;

/// <summary>
/// Headless single-request execution pipeline. Composes a <see cref="RequestItem"/> against
/// its parent <see cref="Collection"/> + folder chain, runs the pre-request script, performs
/// the HTTP call, and runs the post-response + tests scripts. Returns everything the caller
/// needs to render a result without touching any UI types.
///
/// This is the engine behind the Collection Runner, the CLI and the VS Code sidecar. It covers
/// every auth type (OAuth2 authorization-code only when the host allows interactive auth —
/// see <see cref="Options.AllowInteractiveAuth"/>) and every body mode, so a request behaves
/// the same wherever it runs. Host services — a shared OAuth2 token cache, secret-manager
/// resolution, session runtime variables — come in through <see cref="Options"/> and
/// <see cref="Inputs.RuntimeVariables"/>; without them the pipeline is fully self-contained.
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
        RequestComposition.WorkspaceContext? Workspace = null,
        /// <summary>Linked-request context. When null, <c>chain:pre-request</c> links are ignored
        /// (callers without a collection on disk, e.g. ad-hoc sends).</summary>
        ChainContext? Chain = null,
        /// <summary>Session runtime variables (<c>bru.setVar</c> values from earlier sends).
        /// Beat collection/folder vars, lose to script-set and request-level vars. What this
        /// send sets is reported in <see cref="Outputs.RuntimeVariablesSet"/>.</summary>
        IReadOnlyDictionary<string, string>? RuntimeVariables = null);

    /// <summary>Host services for a pipeline run. Every member is optional; the defaults give
    /// the self-contained behavior headless callers (runner, CLI) rely on.</summary>
    public sealed record Options
    {
        public static readonly Options Default = new();

        /// <summary>Token acquirer whose cache should span the host's sends. Null uses a
        /// process-wide acquirer private to the pipeline.</summary>
        public OAuth2TokenAcquirer? OAuth2 { get; init; }

        /// <summary>When true, OAuth2 <c>authorization_code</c> opens the browser (through the
        /// acquirer's <see cref="OAuth2TokenAcquirer.BrowserLauncher"/>) and waits for the
        /// redirect. Headless hosts leave it false and get a clear error instead.</summary>
        public bool AllowInteractiveAuth { get; init; }

        /// <summary>Resolves <c>secret://</c> references in the merged variable bag before
        /// interpolation (typically <c>SecretRegistry.ResolveSecretsAsync</c>).</summary>
        public Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? ResolveSecrets { get; init; }

        /// <summary>Directory relative multipart-file and binary-body paths resolve against
        /// (the collection root). Null sends relative paths as-is.</summary>
        public string? BasePath { get; init; }
    }

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
        /// <summary>Scriptless capture outcomes (<c>vars:post-response[:env|:global]</c>).</summary>
        public IReadOnlyList<CaptureEvaluator.Outcome> Captures { get; init; } = Array.Empty<CaptureEvaluator.Outcome>();
        /// <summary>Number of leading entries in <see cref="Tests"/> that came from scriptless
        /// assertions (the rest are JS <c>test()</c> results).</summary>
        public int AssertionCount { get; init; }
        /// <summary>Linked requests that ran (or were skipped) before this one.</summary>
        public IReadOnlyList<ChainStep> ChainSteps { get; init; } = Array.Empty<ChainStep>();
        /// <summary>Global-environment writes from <c>vars:post-response:global</c> captures and linked requests.</summary>
        public IReadOnlyDictionary<string, string> GlobalVarMutations { get; init; } = EmptyVars;
        /// <summary>Environment variables a script removed with <c>bru.deleteEnvVar</c>.</summary>
        public IReadOnlyList<string> EnvVarDeletions { get; init; } = Array.Empty<string>();
        /// <summary>Runtime variables this send set or changed (<c>bru.setVar</c>, runtime
        /// captures) relative to <see cref="Inputs.RuntimeVariables"/> + request vars — what
        /// a host keeps for later sends. Unlike <see cref="RuntimeVariableMutations"/> it
        /// excludes values that were merely passed in; includes what linked requests captured.</summary>
        public IReadOnlyDictionary<string, string> RuntimeVariablesSet { get; init; } = EmptyVars;
        /// <summary><c>{{name}}</c> placeholders still present in the sent URL, headers or
        /// body — sent as literal text, almost always a missing variable.</summary>
        public IReadOnlyList<string> UnresolvedVariables { get; init; } = Array.Empty<string>();
        /// <summary>The raw executor result (timing, body bytes, content type, sent request
        /// text). Null when the request never went out.</summary>
        public HttpExecutionResult? Response { get; init; }
        /// <summary>The OAuth2 token acquisition result, for hosts that display the token.</summary>
        public OAuth2TokenResult? OAuth2Token { get; init; }

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
        CancellationToken ct = default,
        Options? options = null)
    {
        options ??= Options.Default;
        var sw = Stopwatch.StartNew();
        var composed = RequestComposition.Compose(
            inputs.Collection, inputs.FolderChain, inputs.Request,
            inputs.Workspace ?? RequestComposition.WorkspaceContext.Empty);
        var runtimeIn = inputs.RuntimeVariables ?? EmptyVars;

        // 0. Linked requests (chain:pre-request) — run first; what they capture becomes part of
        //    this request's variable view (runtime → iteration overlay, env/global → env view).
        var consoleAll = new List<ConsoleMessage>();
        IReadOnlyList<ChainStep> chainSteps = Array.Empty<ChainStep>();
        IReadOnlyDictionary<string, string> chainEnv = EmptyVars;
        IReadOnlyDictionary<string, string> chainGlobal = EmptyVars;
        IReadOnlyDictionary<string, string> chainRuntime = EmptyVars;
        if (inputs.Chain is { } chainCtx && composed.ChainLinks.Count > 0)
        {
            var selfCtx = chainCtx.Stack.Count == 0
                ? chainCtx.Push(chainCtx.PathOf(inputs.Request, inputs.FolderChain))
                : chainCtx;
            var chain = await ChainExecutor.RunAsync(composed.ChainLinks, selfCtx,
                inputs.EnvironmentVariables, Merge(inputs.IterationVariables, runtimeIn),
                http, scripting, inputs.Workspace, ct, options).ConfigureAwait(false);
            chainSteps = chain.Steps;
            if (chain.Error is not null)
                return Failure(inputs, composed, sw.ElapsedMilliseconds, chain.Error, consoleAll) with { ChainSteps = chainSteps };
            chainEnv = chain.EnvVariables;
            chainGlobal = chain.GlobalVariables;
            chainRuntime = chain.RuntimeVariables;
            inputs = inputs with
            {
                EnvironmentVariables = Merge(inputs.EnvironmentVariables, chain.EnvVariables, chain.GlobalVariables),
                IterationVariables = Merge(inputs.IterationVariables, chain.RuntimeVariables),
            };
        }

        // The environment as scripts and captures leave it. Starts as the run snapshot; each
        // stage replaces it with the stage's resulting bag (setEnvVar applied, deleteEnvVar
        // removed), so the final diff against the snapshot yields both kinds of change.
        IReadOnlyDictionary<string, string> env = inputs.EnvironmentVariables;
        var collectionView = MergeReadOnly(inputs.IterationVariables, composed.Vars);
        var requestVars = ToDict(inputs.Request.PreRequestVars);

        // 1. Pre-request script — may mutate runtime vars consumed downstream.
        var scriptVars = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(composed.PreRequestScript))
        {
            // The composed view already merged env+collection+folder+request vars. We expose
            // iteration vars as a separate "collection vars" bag so scripts see them as a
            // higher-priority overlay than env without colliding with the request-vars layer
            // that holds bru.setVar() output. Session runtime vars sit under request vars there.
            var pre = scripting.RunPreRequest(
                composed.PreRequestScript!,
                envVars: env,
                collectionVars: collectionView,
                requestVars: Merge(runtimeIn, requestVars),
                request: null,
                cancellationToken: ct);

            consoleAll.AddRange(pre.ConsoleMessages);
            if (!pre.IsSuccess)
            {
                return Failure(inputs, composed, sw.ElapsedMilliseconds,
                    pre.ErrorMessage ?? "Pre-request script failed", consoleAll);
            }
            foreach (var (k, v) in pre.RuntimeVariables) scriptVars[k] = v;
            env = pre.EnvVarMutations;
        }

        // 2. Build the effective variable bag the rest of the pipeline reads. Precedence
        //    (last-wins): env < iterationData < composed (collection ⊕ folders) < session runtime
        //    < scriptRuntime < requestLevel.
        IReadOnlyDictionary<string, string> vars = Merge(
            env,
            inputs.IterationVariables,
            composed.Vars,
            runtimeIn,
            scriptVars,
            requestVars);

        // Pre-resolve secret://provider/path#field values so the synchronous interpolation
        // below sees real secret values. Scripts above saw the references, never the secrets.
        if (options.ResolveSecrets is { } resolveSecrets)
        {
            try { vars = await resolveSecrets(vars, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Failure(inputs, composed, sw.ElapsedMilliseconds, "Canceled.", consoleAll);
            }
            catch (Exception ex)
            {
                return Failure(inputs, composed, sw.ElapsedMilliseconds,
                    $"Failed to resolve secrets: {ex.Message}", consoleAll);
            }
        }

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

        // 4. Resolve auth. OAuth2 is turned into a Bearer/ApiKey auth by acquiring a token
        //    first; static auths become headers / query params here; the signing auths
        //    (SigV4, OAuth1, WSSE) are applied in step 5c once the request is final.
        var configuredAuth = composed.Auth ?? inputs.Request.Auth;
        var authToApply = configuredAuth;
        OAuth2TokenResult? oauth2Token = null;
        if (configuredAuth is { Type: AuthType.OAuth2 })
        {
            var oauth = await PipelineOAuth2
                .ResolveAsync(configuredAuth, vars, ct, options.OAuth2, options.AllowInteractiveAuth)
                .ConfigureAwait(false);
            oauth2Token = oauth.Token;
            if (oauth.Error is not null)
                return Failure(inputs, composed, sw.ElapsedMilliseconds, oauth.Error, consoleAll) with { OAuth2Token = oauth2Token };
            authToApply = oauth.Auth;
        }

        var authResult = AuthApplier.Apply(authToApply, resolvedUrl, vars);
        if (!Uri.TryCreate(authResult.Url, UriKind.Absolute, out var uri))
            return Failure(inputs, composed, sw.ElapsedMilliseconds,
                $"URL is not a valid absolute URI: {authResult.Url}", consoleAll);

        // 5. Compose body + headers (composed-inheritance headers + auth-emitted headers + request-level).
        var composedBody = BodyComposer.Compose(inputs.Request.Body, vars, options.BasePath);
        var body = composedBody.Body;
        var contentType = composedBody.ContentType;

        // 5b. SOAP WS-Security / WS-Addressing: inject the configured headers into the
        //     envelope so runner/CLI sends match the request editor's behavior.
        if (!string.IsNullOrEmpty(body) && SoapSecurityProcessor.HasOutgoing(inputs.Request.Soap))
            body = SoapSecurityProcessor.Apply(body!, inputs.Request.Soap,
                s => Interpolator.Resolve(s, vars));

        var headerList = ComposeHeaders(composed.Headers, vars);
        foreach (var h in authResult.Headers) headerList.Add(h);
        if (!string.IsNullOrEmpty(contentType) && composedBody.FilePath is null
            && !headerList.Any(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headerList.Add(new KeyValuePair<string, string>("Content-Type", contentType!));
        }

        // 5c. Signatures cover the final method / URL / headers / body, so they go last.
        var method = string.IsNullOrEmpty(inputs.Request.Method) ? "GET" : inputs.Request.Method.ToUpperInvariant();
        AuthSigning.AppendSignatureHeaders(configuredAuth, method, uri, headerList, body, vars);

        // {{var}} placeholders that survived interpolation go out as literal text; report them.
        var unresolved = PlaceholderScanner.Collect(uri.ToString(), headerList, body);

        // 6. HTTP send. When settings carry an mTLS client cert, load it here (path and
        //    password support {{var}} interpolation) — a missing/bad cert fails the request
        //    loudly instead of silently sending without it and confusing the user with a
        //    server-side 401/handshake error.
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCert = null;
        if (!string.IsNullOrWhiteSpace(inputs.Request.Settings.MtlsCertPath))
        {
            var certPath = Interpolator.Resolve(inputs.Request.Settings.MtlsCertPath, vars);
            var certPassword = string.IsNullOrEmpty(inputs.Request.Settings.MtlsCertPassword)
                ? null
                : Interpolator.Resolve(inputs.Request.Settings.MtlsCertPassword, vars);
            try
            {
                clientCert = CertificateLoader.LoadClientCertificate(certPath, certPassword);
            }
            catch (Exception ex)
            {
                return Failure(inputs, composed, sw.ElapsedMilliseconds,
                    $"Failed to load mTLS client certificate '{certPath}': {ex.Message}", consoleAll);
            }
        }

        var execRequest = new HttpExecutionRequest(
            Method: new HttpMethod(method),
            Url: uri,
            Headers: headerList,
            Body: body,
            ContentType: contentType,
            MultipartFields: composedBody.MultipartFields,
            FilePath: composedBody.FilePath,
            Options: new HttpRequestOptions(
                FollowRedirects: inputs.Request.Settings.FollowRedirects,
                VerifySsl: inputs.Request.Settings.VerifySsl,
                UseCookies: inputs.Request.Settings.SendCookies && inputs.Request.Settings.SaveCookies,
                NtlmCredential: AuthSigning.NtlmCredential(configuredAuth, vars),
                ClientCertificate: clientCert));

        HttpExecutionResult httpResult;
        try
        {
            httpResult = await http.ExecuteAsync(execRequest, ct).ConfigureAwait(false);

            // Digest: the first leg is the deliberate "ping" that surfaces realm + nonce; answer
            // the challenge once. Final timing and body come from the second response.
            if (AuthSigning.DigestRetryHeader(configuredAuth, httpResult, method, uri, vars) is { } digest)
            {
                headerList.Add(new KeyValuePair<string, string>("Authorization", digest));
                httpResult = await http.ExecuteAsync(execRequest with { Headers = headerList }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(inputs, composed, sw.ElapsedMilliseconds, "Canceled.", consoleAll);
        }
        catch (Exception ex)
        {
            return Failure(inputs, composed, sw.ElapsedMilliseconds, ex.Message, consoleAll);
        }

        // 7. Scriptless captures (vars:post-response[:env|:global]) — before the post-response
        //    script so scripts and assertions see the captured values (Bruno order).
        var snapshot = ScriptlessSteps.Snapshot(httpResult, uri.ToString());
        inputs.Chain?.Cache.Record(inputs.Chain.PathOf(inputs.Request, inputs.FolderChain), snapshot);
        var captures = ScriptlessSteps.RunCaptures(
            ScriptlessSteps.CaptureRules(inputs.Request), snapshot, Merge(vars, scriptVars), scripting, ct);
        foreach (var (k, v) in captures.Runtime) scriptVars[k] = v;
        env = Merge(env, captures.Environment);
        var globalMutations = Merge(chainGlobal, captures.Global);

        Outputs Result(IReadOnlyList<TestOutcome> tests, string? error, int assertionCount) =>
            new(
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
                EnvVarMutations: Merge(chainEnv, EnvVarDelta(inputs.EnvironmentVariables, env)),
                ErrorMessage: error)
            {
                Captures = captures.Outcomes,
                AssertionCount = assertionCount,
                ChainSteps = chainSteps,
                GlobalVarMutations = globalMutations,
                EnvVarDeletions = EnvVarRemovals(inputs.EnvironmentVariables, env),
                RuntimeVariablesSet = Merge(chainRuntime, EnvVarDelta(Merge(runtimeIn, requestVars), scriptVars)),
                UnresolvedVariables = unresolved,
                Response = httpResult,
                OAuth2Token = oauth2Token,
            };

        // 8. Post-response script + tests. Combined-run, mirrors editor behavior.
        IReadOnlyList<TestOutcome> tests = Array.Empty<TestOutcome>();
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
                envVars: env,
                collectionVars: collectionView,
                requestVars: Merge(runtimeIn, scriptVars),
                request: null,
                cancellationToken: ct);

            consoleAll.AddRange(post.ConsoleMessages);
            tests = post.TestOutcomes;
            env = post.EnvVarMutations;

            foreach (var (k, v) in post.RuntimeVariables) scriptVars[k] = v;

            if (!post.IsSuccess && tests.Count == 0 && composed.Assertions.Count == 0)
                return Result(tests, post.ErrorMessage, 0);
        }

        // 9. Scriptless assertions — evaluated against the final variable view; listed ahead of
        //    the JS test() results.
        var assertions = ScriptlessSteps.RunAssertions(
            composed.Assertions, snapshot, Merge(vars, scriptVars), scripting, ct);
        if (assertions.Count > 0)
            tests = assertions.Select(a => a.ToTestOutcome()).Concat(tests).ToList();

        return Result(tests, httpResult.ErrorMessage, assertions.Count);
    }

    // ----- helpers ----------------------------------------------------------

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

    /// <summary>Keys of <paramref name="before"/> missing from <paramref name="after"/> —
    /// the <c>deleteEnvVar</c> calls a script made.</summary>
    private static IReadOnlyList<string> EnvVarRemovals(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) =>
        before.Keys.Where(k => !after.ContainsKey(k)).ToList();

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
