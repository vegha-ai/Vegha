using Jint;
using Jint.Runtime;

namespace Vegha.Core.Scripting;

/// <summary>
/// Runs user scripts in a sandboxed Jint engine. Constraints: 64 MB memory, 10 s wall clock,
/// 64-deep recursion. No file system, no <c>setTimeout</c>/<c>setInterval</c>. A Postman-
/// compatible <c>require(...)</c> (see <see cref="JsModules"/>) exposes bundled libraries only —
/// there is no file/network module loading.
/// </summary>
public sealed class JintHost
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    public const long DefaultMemoryLimit = 64L * 1024 * 1024;
    public const int DefaultRecursionLimit = 64;

    private readonly TimeSpan _timeout;
    private readonly long _memoryLimit;
    private readonly int _recursionLimit;
    private readonly IBruRequestExecutor? _subRequestExecutor;
    private readonly IBruCookieJar? _cookieJar;

    public JintHost(
        TimeSpan? timeout = null,
        long? memoryLimit = null,
        int? recursionLimit = null,
        IBruRequestExecutor? subRequestExecutor = null,
        IBruCookieJar? cookieJar = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _memoryLimit = memoryLimit ?? DefaultMemoryLimit;
        _recursionLimit = recursionLimit ?? DefaultRecursionLimit;
        _subRequestExecutor = subRequestExecutor;
        _cookieJar = cookieJar;
    }

    /// <summary>Runs a pre-request script, returning the variable mutations the script performed
    /// plus runner state and any mutations the script made to the in-flight request via <c>req</c>.</summary>
    public PreRequestResult RunPreRequest(
        string script,
        IReadOnlyDictionary<string, string> envVars,
        IReadOnlyDictionary<string, string>? collectionVars = null,
        IReadOnlyDictionary<string, string>? requestVars = null,
        RequestApi? request = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = new Dictionary<string, string>();
        if (requestVars is not null)
            foreach (var (k, v) in requestVars) runtime[k] = v;

        var bru = new BruApi(runtime, envVars, collectionVars ?? new Dictionary<string, string>(), _subRequestExecutor, _cookieJar);
        var console = new ConsoleSink();

        var result = RunInternal(script, cancellationToken, engine =>
        {
            engine.SetValue("bru", bru);
            engine.SetValue("console", new ConsoleApi(console));
            if (request is not null) engine.SetValue("req", request);
        }, runtime, testApi: null);

        return new PreRequestResult(
            result.IsSuccess,
            result.ErrorMessage,
            result.RuntimeVariables,
            new Dictionary<string, string>(bru.EnvVarMutations),
            bru.RunState,
            console.Messages);
    }

    /// <summary>
    /// Runs a post-response script and/or a tests block. Exposes <c>bru</c>, <c>req</c>, <c>res</c>,
    /// <c>test(name, fn)</c>, and <c>expect(actual)</c>. Returns variable mutations, env var
    /// mutations, runner state, and test outcomes.
    /// </summary>
    public PostResponseResult RunPostResponse(
        string? postScript,
        string? testsScript,
        ResponseApi response,
        IReadOnlyDictionary<string, string> envVars,
        IReadOnlyDictionary<string, string>? collectionVars = null,
        IReadOnlyDictionary<string, string>? requestVars = null,
        RequestApi? request = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = new Dictionary<string, string>();
        if (requestVars is not null)
            foreach (var (k, v) in requestVars) runtime[k] = v;

        var bru = new BruApi(runtime, envVars, collectionVars ?? new Dictionary<string, string>(), _subRequestExecutor, _cookieJar);
        var testApi = new TestApi();
        var console = new ConsoleSink();

        var combined = string.Join("\n", new[] { postScript, testsScript }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrEmpty(combined))
            return new PostResponseResult(true, null, runtime,
                new Dictionary<string, string>(bru.EnvVarMutations), bru.RunState,
                Array.Empty<TestOutcome>(), Array.Empty<ConsoleMessage>());

        var script = RunInternal(combined, cancellationToken, engine =>
        {
            engine.SetValue("bru", bru);
            engine.SetValue("res", response);
            engine.SetValue("console", new ConsoleApi(console));
            if (request is not null) engine.SetValue("req", request);
            engine.SetValue("test", new Action<string, Action>(testApi.test));
            engine.SetValue("expect", new Func<object?, Expectation>(testApi.expect));
        }, runtime, testApi);

        return new PostResponseResult(
            script.IsSuccess,
            script.ErrorMessage,
            script.RuntimeVariables,
            new Dictionary<string, string>(bru.EnvVarMutations),
            bru.RunState,
            testApi.Outcomes,
            console.Messages);
    }

    /// <summary>Evaluates a single JavaScript expression against a Bruno-shaped <c>res</c>
    /// (<c>res.body</c> is the parsed JSON, <c>res.headers</c> a lower-cased object) and a
    /// read-only <c>bru</c>. This is the fallback for scriptless captures / assertions whose
    /// expression isn't in the native grammar (e.g. <c>res.body.items.find(i =&gt; i.main).id</c>).
    /// Throws <see cref="Testing.FilterException"/> with a readable message on failure.</summary>
    public Testing.ResolvedValue EvaluateResponseExpression(
        string expression,
        Testing.ResponseSnapshot response,
        IReadOnlyDictionary<string, string>? vars = null,
        CancellationToken cancellationToken = default)
    {
        var engine = new Engine(o => o
            .LimitMemory(_memoryLimit)
            .TimeoutInterval(_timeout)
            .LimitRecursion(_recursionLimit)
            .CancellationToken(cancellationToken));
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in response.Headers)
        {
            var key = k.ToLowerInvariant();
            headers[key] = headers.TryGetValue(key, out var prev) ? prev + ", " + v : v;
        }
        var runtime = new Dictionary<string, string>(vars ?? new Dictionary<string, string>());
        engine.SetValue("bru", new BruApi(runtime, vars ?? new Dictionary<string, string>(),
            new Dictionary<string, string>(), null, null));
        engine.SetValue("__status", response.Status);
        engine.SetValue("__statusText", response.StatusText);
        engine.SetValue("__time", response.ElapsedMilliseconds);
        engine.SetValue("__bodyText", response.Body);
        engine.SetValue("__headersJson", System.Text.Json.JsonSerializer.Serialize(headers));
        try
        {
            engine.Execute("""
                var __b; try { __b = JSON.parse(__bodyText); } catch (e) { __b = __bodyText; }
                var __h = JSON.parse(__headersJson);
                var res = {
                  status: __status, statusText: __statusText, responseTime: __time,
                  headers: __h, body: __b, text: __bodyText,
                  getStatus: function () { return __status; },
                  getStatusText: function () { return __statusText; },
                  getResponseTime: function () { return __time; },
                  getBody: function () { return __b; },
                  getBodyAsText: function () { return __bodyText; },
                  getHeaders: function () { return __h; },
                  getHeader: function (n) { return __h[String(n).toLowerCase()]; }
                };
                """);
            engine.Execute(JsPreloads.Source);
            var json = engine.Evaluate("JSON.stringify((" + expression + "\n))");
            if (json.IsUndefined()) return Testing.ResolvedValue.Missing;
            return Testing.ResolvedValue.Of(System.Text.Json.Nodes.JsonNode.Parse(json.AsString()));
        }
        catch (JavaScriptException jex)
        {
            throw new Testing.FilterException("Expression error: " + jex.Message);
        }
        catch (Acornima.ParseErrorException pex)
        {
            throw new Testing.FilterException("Expression syntax error: " + pex.Description);
        }
        catch (TimeoutException)
        {
            throw new Testing.FilterException($"Expression exceeded {_timeout.TotalSeconds:F1}s timeout");
        }
    }

    private ScriptResult RunInternal(
        string script,
        CancellationToken cancellationToken,
        Action<Engine> setup,
        IReadOnlyDictionary<string, string> runtime,
        TestApi? testApi)
    {
        var engine = new Engine(o => o
            .LimitMemory(_memoryLimit)
            .TimeoutInterval(_timeout)
            .LimitRecursion(_recursionLimit)
            .CancellationToken(cancellationToken));
        // Correctness-critical backing for the require() module shims (crypto, XML, base64,
        // uuid). Bound before the preloads run since JsModules references it as `__vegha`.
        engine.SetValue("__vegha", new ScriptModuleHost());
        setup(engine);

        try
        {
            // Inject lodash-lite + axios shim so user scripts can write `_.get(...)` and
            // `axios.get(...)` without bringing their own polyfills, then the Postman-
            // compatible require() registry + globals (xml2Json / atob / btoa).
            engine.Execute(JsPreloads.Source);
            engine.Execute(JsModules.Source);

            engine.Execute(script ?? string.Empty);
            return ScriptResult.Success(runtime);
        }
        catch (JavaScriptException jex)
        {
            // Surface line/col when Jint resolves a Location for the throw site.
            var loc = jex.Location;
            var locText = loc.Start.Line > 0
                ? $" at line {loc.Start.Line}, col {loc.Start.Column + 1}"
                : string.Empty;
            return ScriptResult.Failure($"Script error{locText}: {jex.Message}", runtime);
        }
        catch (TimeoutException)
        {
            return ScriptResult.Failure(
                $"Script exceeded {_timeout.TotalSeconds:F1}s timeout", runtime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ScriptResult.Failure("Script canceled", runtime);
        }
        catch (Exception ex)
        {
            return ScriptResult.Failure($"Script host error: {ex.Message}", runtime);
        }
    }
}

/// <summary>Outcome of running a pre-request script.</summary>
public sealed record PreRequestResult(
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> RuntimeVariables,
    IReadOnlyDictionary<string, string> EnvVarMutations,
    BruRunState RunState,
    IReadOnlyList<ConsoleMessage> ConsoleMessages);

/// <summary>Outcome of running post-response + tests scripts.</summary>
public sealed record PostResponseResult(
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> RuntimeVariables,
    IReadOnlyDictionary<string, string> EnvVarMutations,
    BruRunState RunState,
    IReadOnlyList<TestOutcome> TestOutcomes,
    IReadOnlyList<ConsoleMessage> ConsoleMessages);

/// <summary>The outcome of running a script: ok-or-error plus the variable mutations performed.</summary>
public sealed record ScriptResult(
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> RuntimeVariables)
{
    public static ScriptResult Success(IReadOnlyDictionary<string, string> runtime) =>
        new(true, null, runtime);
    public static ScriptResult Failure(string error, IReadOnlyDictionary<string, string> runtime) =>
        new(false, error, runtime);
}
