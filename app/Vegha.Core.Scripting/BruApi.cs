namespace Vegha.Core.Scripting;

/// <summary>
/// The <c>bru</c> object exposed to user scripts. Mirrors the surface from
/// <c>bruno-js/src/bru.js</c> — variable get/set across runtime/env/collection/global
/// scopes, sub-requests, runner controls, cookie jar access, interpolation, utils, and
/// per-run state used by the collection runner (setNextRequest, skipRequest, stopExecution).
///
/// "Global env vars" map to env vars in Vegha's flatter model — there's no separate
/// global scope, but the API shape is preserved so Postman-translated scripts that call
/// <c>bru.getGlobalEnvVar</c> still work.
/// </summary>
public sealed class BruApi
{
    private readonly Dictionary<string, string> _runtimeVars;
    private readonly Dictionary<string, string> _envVars;
    private readonly Dictionary<string, string> _collectionVars;
    private readonly IBruRequestExecutor? _subRequestExecutor;

    /// <summary>Per-run state captured for the collection runner (or whoever drives
    /// chained requests). UI/host code reads these after each request executes.</summary>
    public BruRunState RunState { get; } = new();

    /// <summary>Mutations to environment variables performed by the script.</summary>
    public IReadOnlyDictionary<string, string> EnvVarMutations => _envVars;

    /// <summary><c>bru.runner.*</c> facade — Jint binds it as a property on bru.</summary>
    public BruRunnerApi runner { get; }

    /// <summary><c>bru.cookies.jar()</c> entry point. Throws if no jar was supplied.</summary>
    public BruCookiesApi cookies { get; }

    /// <summary><c>bru.utils.minifyJson / minifyXml</c> helpers.</summary>
    public BruUtilsApi utils { get; } = new();

    public BruApi(
        Dictionary<string, string> runtimeVars,
        IReadOnlyDictionary<string, string> envVars,
        IReadOnlyDictionary<string, string> collectionVars,
        IBruRequestExecutor? subRequestExecutor = null,
        IBruCookieJar? cookieJar = null)
    {
        _runtimeVars = runtimeVars;
        _envVars = new Dictionary<string, string>(envVars);
        _collectionVars = new Dictionary<string, string>(collectionVars);
        _subRequestExecutor = subRequestExecutor;
        runner = new BruRunnerApi(RunState);
        cookies = new BruCookiesApi(cookieJar);
    }

    // ---- Runtime (request-scoped) variables ----

    /// <summary>Sets a runtime variable for this request execution.</summary>
    public void setVar(string name, string value) => _runtimeVars[name] = value;

    /// <summary>Reads a runtime variable, falling back through env and collection if missing.</summary>
    public string? getVar(string name) =>
        _runtimeVars.TryGetValue(name, out var v) ? v
        : _envVars.TryGetValue(name, out v) ? v
        : _collectionVars.TryGetValue(name, out v) ? v
        : null;

    /// <summary>Removes a runtime variable (Bruno parity).</summary>
    public void deleteVar(string name) => _runtimeVars.Remove(name);

    // ---- Environment variables ----

    /// <summary>Sets an environment variable. Surfaced as a mutation the host can
    /// persist to the active environment file when the script finishes.</summary>
    public void setEnvVar(string name, string value)
    {
        _envVars[name] = value;
        // Also visible to subsequent getVar calls within this script.
        _runtimeVars[name] = value;
    }

    public string? getEnvVar(string name) =>
        _envVars.TryGetValue(name, out var v) ? v : null;

    public bool hasEnvVar(string name) => _envVars.ContainsKey(name);

    public void deleteEnvVar(string name) => _envVars.Remove(name);

    // ---- Collection variables ----

    public string? getCollectionVar(string name) =>
        _collectionVars.TryGetValue(name, out var v) ? v : null;

    public void setCollectionVar(string name, string value) => _collectionVars[name] = value;

    public bool hasCollectionVar(string name) => _collectionVars.ContainsKey(name);

    public void deleteCollectionVar(string name) => _collectionVars.Remove(name);

    // ---- Global env variables (alias to env in Vegha's flatter model) ----
    // Vegha doesn't separate global from per-environment vars — Postman translations
    // route global ops through the same env bag so scripts that call
    // bru.getGlobalEnvVar() still see the expected values.

    public string? getGlobalEnvVar(string name) => getEnvVar(name);
    public void setGlobalEnvVar(string name, string value) => setEnvVar(name, value);
    public bool hasGlobalEnvVar(string name) => hasEnvVar(name);
    public void deleteGlobalEnvVar(string name) => deleteEnvVar(name);
    public Dictionary<string, string> getAllGlobalEnvVars() => new(_envVars, StringComparer.Ordinal);

    // ---- Process env (read-only) — mirrors Bruno ----

    public string? getProcessEnv(string name) => System.Environment.GetEnvironmentVariable(name);

    // ---- Read-only views (request/folder vars) ----
    // These need data injection; in the current API surface request vars feed into the
    // runtime bag, so getRequestVar resolves there. Folder vars likewise feed into runtime.

    /// <summary>Returns the value of a request-scoped variable (read-only view of the runtime
    /// bag, which is seeded from <c>vars:pre-request</c>).</summary>
    public string? getRequestVar(string name) =>
        _runtimeVars.TryGetValue(name, out var v) ? v : null;

    /// <summary>Returns the value of a folder-scoped variable. Vegha flattens folder
    /// vars into the runtime bag at compose time, so this is a read of that bag.</summary>
    public string? getFolderVar(string name) =>
        _runtimeVars.TryGetValue(name, out var v) ? v : null;

    // ---- Interpolation ----

    /// <summary>Replaces <c>{{name}}</c> placeholders in <paramref name="template"/> against
    /// the current variable bag (runtime + env + collection). Postman dynamic vars like
    /// <c>{{$randomUUID}}</c> resolve too. Translation target for <c>pm.variables.replaceIn</c>.</summary>
    public string interpolate(string template)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _collectionVars) merged[kv.Key] = kv.Value;
        foreach (var kv in _envVars) merged[kv.Key] = kv.Value;
        foreach (var kv in _runtimeVars) merged[kv.Key] = kv.Value;
        return Vegha.Core.Interpolation.Interpolator.Resolve(template, merged);
    }

    // ---- Runner / chaining ----

    /// <summary>Tells the collection runner the next request to execute (by name).
    /// <c>null</c> means "stop chaining". Has no effect outside a runner context.</summary>
    public void setNextRequest(string? name) => RunState.NextRequestName = name;

    // ---- Sub-requests ----

    /// <summary>Synchronously sends an HTTP request from inside a script. Throws if
    /// the host hasn't supplied an executor (e.g., scripts running outside the app).</summary>
    public BruRequestResult sendRequest(BruRequestOptions options)
    {
        if (_subRequestExecutor is null)
            throw new InvalidOperationException("bru.sendRequest is not available in this context.");
        return _subRequestExecutor.Send(options);
    }

    /// <summary>Bruno's <c>bru.runRequest(pathname)</c> — invokes another request from the
    /// collection by file path. Not yet implemented; throws with a clear message so users
    /// see what's missing instead of a silent no-op.</summary>
    public BruRequestResult runRequest(string pathname) =>
        throw new NotImplementedException(
            "bru.runRequest is not yet implemented — use bru.sendRequest with a literal URL for now.");

    // ---- Sleep ----

    /// <summary>Blocks the current script for <paramref name="ms"/> milliseconds. The Jint
    /// timeout still applies (default 10s), so a script can't sleep past the host limit.</summary>
    public void sleep(double ms)
    {
        if (ms <= 0) return;
        // Truncate to int; ms > int.MaxValue makes no sense under the wall-clock cap anyway.
        Thread.Sleep((int)Math.Min(ms, int.MaxValue));
    }
}

/// <summary>Tiny utility surface — Bruno exposes minify helpers under <c>bru.utils</c>.</summary>
public sealed class BruUtilsApi
{
    /// <summary>Minifies JSON by parsing and re-serializing without whitespace. Returns the
    /// input unchanged when parsing fails (matches Bruno's defensive behavior).</summary>
    public string minifyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json ?? string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        }
        catch { return json; }
    }

    /// <summary>Strips whitespace between XML tags. Naive but matches Bruno's helper —
    /// useful for compact SOAP payloads and snapshot comparisons.</summary>
    public string minifyXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return xml ?? string.Empty;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            return doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        }
        catch { return xml; }
    }
}

/// <summary>Mutations the script asked the runner to apply. Read by the host after
/// the script finishes; never inspected by the script itself.</summary>
public sealed class BruRunState
{
    public string? NextRequestName { get; set; }
    public bool SkipRequest { get; set; }
    public bool StopExecution { get; set; }
}

public sealed class BruRunnerApi
{
    private readonly BruRunState _state;
    public BruRunnerApi(BruRunState state) { _state = state; }

    /// <summary>Mark the current request as skipped. The collection runner advances
    /// to the next request without sending this one.</summary>
    public void skipRequest() => _state.SkipRequest = true;

    /// <summary>Stop the run immediately after the current request returns.</summary>
    public void stopExecution() => _state.StopExecution = true;
}
