namespace Vegha.Core.Scripting;

/// <summary>Which script slot is being edited/run. Gates which objects are in scope:
/// pre-request has no <c>res</c>/<c>test</c>/<c>expect</c>; post-response and tests do.</summary>
public enum ScriptKind
{
    PreRequest,
    PostResponse,
    Tests,
}

/// <summary>Kind of a completion entry — drives the popup icon/label and whether the editor
/// inserts call parens.</summary>
public enum ScriptMemberKind
{
    /// <summary>A nested object reached through this member (e.g. <c>bru.runner</c>).</summary>
    Object,
    Method,
    Property,
}

/// <summary>One completable member of a script object.</summary>
/// <param name="Name">JS identifier as typed (matches the C# API member name 1:1).</param>
/// <param name="Kind">Method / Property / Object.</param>
/// <param name="Signature">Short signature shown in the popup row, e.g. <c>setVar(name, value)</c>.</param>
/// <param name="Description">One-line description shown in the side detail panel.</param>
/// <param name="ReturnsObject">When set, the key in <see cref="ScriptApiCatalog.AllObjects"/> this
/// member resolves to, so chained member completion (<c>bru.runner.</c>) works.</param>
public sealed record ScriptMember(
    string Name,
    ScriptMemberKind Kind,
    string Signature,
    string Description,
    string? ReturnsObject = null);

/// <summary>A completable script object (top-level like <c>bru</c>/<c>res</c>, or nested like
/// <c>bru.runner</c>/<c>Expectation</c>).</summary>
public sealed record ScriptObject(
    string Name,
    string Description,
    IReadOnlyList<ScriptMember> Members);

/// <summary>
/// Static model of the JavaScript surface exposed to user scripts — the single source of truth
/// behind editor autocomplete. Mirrors the runtime objects bound in <see cref="JintHost"/>
/// (<c>bru</c>/<c>req</c>/<c>res</c>/<c>console</c>/<c>test</c>/<c>expect</c>) plus the
/// <c>_</c> (lodash-lite) and <c>axios</c> preloads from <see cref="JsPreloads"/>.
///
/// Member names are kept in lockstep with the real API classes by
/// <c>ScriptApiCatalogTests</c>, which reflects over <see cref="BruApi"/>/<see cref="RequestApi"/>/
/// <see cref="ResponseApi"/> and fails if a public member is missing here.
/// </summary>
public static class ScriptApiCatalog
{
    /// <summary>Every object keyed by its catalog name. Top-level names are bare identifiers
    /// (<c>bru</c>); nested objects use dotted/synthetic keys (<c>bru.runner</c>, <c>Expectation</c>,
    /// <c>ChaiChain</c>) referenced via <see cref="ScriptMember.ReturnsObject"/>.</summary>
    public static IReadOnlyDictionary<string, ScriptObject> AllObjects { get; } = Build();

    // Top-level identifiers visible in each script kind.
    private static readonly string[] PreRequestGlobals = { "bru", "req", "console", "_", "axios", "require", "xml2Json", "atob", "btoa" };
    private static readonly string[] PostGlobals = { "bru", "req", "res", "test", "expect", "console", "_", "axios", "require", "xml2Json", "atob", "btoa" };

    /// <summary>Objects offered by Ctrl+Space at the top level for the given script kind, sorted by name.</summary>
    public static IReadOnlyList<ScriptObject> TopLevel(ScriptKind kind)
    {
        var names = kind == ScriptKind.PreRequest ? PreRequestGlobals : PostGlobals;
        return names.Select(n => AllObjects[n]).OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>True when <paramref name="name"/> is a top-level identifier in scope for the kind.</summary>
    public static bool IsTopLevel(string name, ScriptKind kind) =>
        (kind == ScriptKind.PreRequest ? PreRequestGlobals : PostGlobals).Contains(name);

    /// <summary>Resolves the object whose members should be listed after a member-access
    /// expression (the text immediately before the typed <c>.</c>). Handles dotted chains
    /// (<c>bru.runner</c>) and call expressions (<c>expect(x)</c> → Expectation, <c>.to</c> →
    /// ChaiChain). Returns null when the expression doesn't resolve to a known object, or when
    /// the head identifier isn't in scope for <paramref name="kind"/>.</summary>
    public static ScriptObject? Resolve(string accessExpression, ScriptKind kind)
    {
        if (string.IsNullOrWhiteSpace(accessExpression)) return null;

        var segments = SplitTopLevelDots(accessExpression);
        if (segments.Count == 0) return null;

        // Head segment establishes the starting object.
        var (headName, headWasCall) = StripCall(segments[0]);
        if (!IsTopLevel(headName, kind)) return null;

        ScriptObject? current;
        if (headName == "expect")
        {
            // expect is a function; only meaningful when actually called.
            current = headWasCall ? AllObjects.GetValueOrDefault("Expectation") : null;
        }
        else if (headName == "test")
        {
            current = null; // test(name, fn) has no chainable surface.
        }
        else
        {
            current = AllObjects.GetValueOrDefault(headName);
        }

        // Walk the remaining segments, hopping to ReturnsObject targets.
        for (var i = 1; i < segments.Count && current is not null; i++)
        {
            var (memberName, _) = StripCall(segments[i]);
            var member = current.Members.FirstOrDefault(m =>
                string.Equals(m.Name, memberName, StringComparison.Ordinal));
            current = member?.ReturnsObject is { } key ? AllObjects.GetValueOrDefault(key) : null;
        }

        return current;
    }

    // ---- expression parsing helpers ----

    /// <summary>Splits a member-access expression on top-level dots, ignoring dots nested inside
    /// parentheses/brackets (so <c>expect(a.b).to</c> → ["expect(a.b)", "to"]).</summary>
    private static List<string> SplitTopLevelDots(string expr)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { if (depth > 0) depth--; }
            else if (c == '.' && depth == 0)
            {
                parts.Add(expr[start..i]);
                start = i + 1;
            }
        }
        parts.Add(expr[start..]);
        return parts;
    }

    /// <summary>Reduces a segment like <c>getBody()</c> or <c>expect(x)</c> to its identifier plus
    /// a flag indicating whether it was a call.</summary>
    private static (string Name, bool WasCall) StripCall(string segment)
    {
        var paren = segment.IndexOf('(');
        if (paren < 0) return (segment.Trim(), false);
        return (segment[..paren].Trim(), true);
    }

    // ---- catalog data ----

    private static Dictionary<string, ScriptObject> Build()
    {
        var objects = new List<ScriptObject>
        {
            new("bru", "Vegha runtime: variables, env, chaining, sub-requests, cookies, utils.", new[]
            {
                M("getVar", "getVar(name)", "Read a runtime variable (falls back to env, then collection)."),
                M("setVar", "setVar(name, value)", "Set a runtime variable for this request execution."),
                M("deleteVar", "deleteVar(name)", "Remove a runtime variable."),
                M("getEnvVar", "getEnvVar(name)", "Read an environment variable."),
                M("setEnvVar", "setEnvVar(name, value)", "Set an environment variable (persisted to the active env after the run)."),
                M("hasEnvVar", "hasEnvVar(name)", "True if the environment variable exists."),
                M("deleteEnvVar", "deleteEnvVar(name)", "Remove an environment variable."),
                M("getCollectionVar", "getCollectionVar(name)", "Read a collection-scoped variable."),
                M("setCollectionVar", "setCollectionVar(name, value)", "Set a collection-scoped variable."),
                M("hasCollectionVar", "hasCollectionVar(name)", "True if the collection variable exists."),
                M("deleteCollectionVar", "deleteCollectionVar(name)", "Remove a collection-scoped variable."),
                M("getGlobalEnvVar", "getGlobalEnvVar(name)", "Read a global env variable (aliases env in Vegha)."),
                M("setGlobalEnvVar", "setGlobalEnvVar(name, value)", "Set a global env variable (aliases env in Vegha)."),
                M("hasGlobalEnvVar", "hasGlobalEnvVar(name)", "True if the global env variable exists."),
                M("deleteGlobalEnvVar", "deleteGlobalEnvVar(name)", "Remove a global env variable."),
                M("getAllGlobalEnvVars", "getAllGlobalEnvVars()", "Return all global env variables as an object."),
                M("getProcessEnv", "getProcessEnv(name)", "Read a host process environment variable (read-only)."),
                M("getRequestVar", "getRequestVar(name)", "Read a request-scoped variable."),
                M("getFolderVar", "getFolderVar(name)", "Read a folder-scoped variable."),
                M("interpolate", "interpolate(template)", "Resolve {{name}} placeholders against the current variable set."),
                M("setNextRequest", "setNextRequest(name)", "Tell the collection runner which request to run next (null stops)."),
                M("sendRequest", "sendRequest(options)", "Send an HTTP request synchronously from the script."),
                M("runRequest", "runRequest(pathname)", "Run another request from the collection by path."),
                M("sleep", "sleep(ms)", "Block the script for the given milliseconds (within the host timeout)."),
                Obj("runner", "runner", "Collection-runner controls.", "bru.runner"),
                Obj("cookies", "cookies", "Cookie-jar access.", "bru.cookies"),
                Obj("utils", "utils", "JSON/XML minify helpers.", "bru.utils"),
            }),

            new("bru.runner", "Collection-runner controls.", new[]
            {
                M("skipRequest", "skipRequest()", "Skip the current request in a run."),
                M("stopExecution", "stopExecution()", "Stop the run after the current request."),
            }),

            new("bru.cookies", "Cookie-jar access.", new[]
            {
                M("jar", "jar()", "Return the cookie jar for get/set/clear operations."),
            }),

            new("bru.utils", "JSON/XML minify helpers.", new[]
            {
                M("minifyJson", "minifyJson(json)", "Return JSON with whitespace stripped."),
                M("minifyXml", "minifyXml(xml)", "Return XML with inter-tag whitespace stripped."),
            }),

            new("req", "The outgoing request — mutate before send.", new[]
            {
                M("getMethod", "getMethod()", "Return the HTTP method."),
                M("setMethod", "setMethod(method)", "Set the HTTP method."),
                M("getUrl", "getUrl()", "Return the request URL."),
                M("setUrl", "setUrl(url)", "Set the request URL."),
                M("getBody", "getBody()", "Return the request body."),
                M("setBody", "setBody(body)", "Set the request body."),
                M("getName", "getName()", "Return the request name."),
                M("getHost", "getHost()", "Return the URL host."),
                M("getPath", "getPath()", "Return the URL path."),
                M("getQueryString", "getQueryString()", "Return the query string (without the leading ?)."),
                M("getPathParams", "getPathParams()", "Return declared path params as an object."),
                M("getHeader", "getHeader(name)", "Return a request header value (or null)."),
                M("setHeader", "setHeader(name, value)", "Set a request header."),
                M("removeHeader", "removeHeader(name)", "Remove a request header."),
                M("getHeaders", "getHeaders()", "Return all request headers as an object."),
                Prop("headerList", "headerList", "PropertyList facade: add/upsert/remove/each/filter/map."),
            }),

            new("res", "The response — available in post-response and tests.", new[]
            {
                Prop("status", "status: number", "HTTP status code."),
                Prop("statusText", "statusText: string", "HTTP status text."),
                Prop("body", "body: string", "Raw response body."),
                Prop("responseTime", "responseTime: number", "Response time in milliseconds."),
                Prop("url", "url: string", "Final response URL."),
                Prop("headerList", "headerList", "Read-only PropertyList facade over response headers."),
                M("getStatus", "getStatus()", "Return the status code."),
                M("getStatusText", "getStatusText()", "Return the status text."),
                M("getBody", "getBody()", "Return the parsed body (object when JSON) or the raw string."),
                M("getBodyAsText", "getBodyAsText()", "Return the raw response body as text."),
                M("getHeaders", "getHeaders()", "Return all response headers as an object."),
                M("getHeader", "getHeader(name)", "Return a response header value (or null)."),
                M("hasHeader", "hasHeader(name)", "True if the response has the header."),
                M("getResponseTime", "getResponseTime()", "Return the response time in milliseconds."),
                M("getUrl", "getUrl()", "Return the final response URL."),
                M("getSize", "getSize()", "Return { header, body, total } byte sizes."),
            }),

            new("console", "Console logging — captured into the run output.", new[]
            {
                M("log", "log(...args)", "Log a message."),
                M("info", "info(...args)", "Log an info message."),
                M("warn", "warn(...args)", "Log a warning."),
                M("error", "error(...args)", "Log an error."),
                M("debug", "debug(...args)", "Log a debug message."),
            }),

            new("test", "Define a test: test(name, fn).", Array.Empty<ScriptMember>()),

            new("expect", "Start an assertion: expect(actual).", Array.Empty<ScriptMember>()),

            new("Expectation", "Assertion matchers (Jest-style) returned by expect(actual).", new[]
            {
                Obj("not", "not", "Negate the next matcher.", "Expectation"),
                Obj("to", "to", "Chai-style chain entry point.", "ChaiChain"),
                M("toBe", "toBe(expected)", "Assert strict equality."),
                M("toEqual", "toEqual(expected)", "Assert equality."),
                M("toContain", "toContain(substring)", "Assert the value contains the substring."),
                M("toHaveLength", "toHaveLength(n)", "Assert length equals n."),
                M("toBeNull", "toBeNull()", "Assert the value is null."),
                M("toBeUndefined", "toBeUndefined()", "Assert the value is undefined."),
                M("toBeDefined", "toBeDefined()", "Assert the value is defined."),
                M("toBeTruthy", "toBeTruthy()", "Assert the value is truthy."),
                M("toBeFalsy", "toBeFalsy()", "Assert the value is falsy."),
                M("toBeGreaterThan", "toBeGreaterThan(n)", "Assert value > n."),
                M("toBeGreaterThanOrEqual", "toBeGreaterThanOrEqual(n)", "Assert value >= n."),
                M("toBeLessThan", "toBeLessThan(n)", "Assert value < n."),
                M("toBeLessThanOrEqual", "toBeLessThanOrEqual(n)", "Assert value <= n."),
                M("toMatch", "toMatch(pattern)", "Assert the value matches the regex pattern."),
                M("toMatchSchema", "toMatchSchema(schema)", "Assert the value validates against a JSON Schema."),
                M("toThrow", "toThrow()", "Assert the wrapped function throws."),
            }),

            new("ChaiChain", "Chai-style assertion chain (expect(x).to...).", new[]
            {
                Obj("to", "to", "Chain word.", "ChaiChain"),
                Obj("be", "be", "Chain word.", "ChaiChain"),
                Obj("been", "been", "Chain word.", "ChaiChain"),
                Obj("have", "have", "Chain word.", "ChaiChain"),
                Obj("has", "has", "Chain word.", "ChaiChain"),
                Obj("with", "with", "Chain word.", "ChaiChain"),
                Obj("that", "that", "Chain word.", "ChaiChain"),
                Obj("which", "which", "Chain word.", "ChaiChain"),
                Obj("and", "and", "Chain word.", "ChaiChain"),
                Obj("is", "is", "Chain word.", "ChaiChain"),
                Obj("not", "not", "Negate the assertion.", "ChaiChain"),
                Obj("deep", "deep", "Switch following comparisons to deep equality.", "ChaiChain"),
                Prop("true", "true", "Assert the value is true."),
                Prop("false", "false", "Assert the value is false."),
                Prop("null", "null", "Assert the value is null."),
                Prop("undefined", "undefined", "Assert the value is undefined."),
                Prop("exist", "exist", "Assert the value is non-null."),
                Prop("empty", "empty", "Assert the value is empty."),
                Prop("ok", "ok", "Assert the value is truthy."),
                M("equal", "equal(expected)", "Assert equality."),
                M("eql", "eql(expected)", "Assert deep equality."),
                M("a", "a(typeName)", "Assert the runtime type (string/number/array/...)."),
                M("an", "an(typeName)", "Assert the runtime type (string/number/array/...)."),
                M("property", "property(name)", "Assert a property exists and re-target the chain at it."),
                M("jsonBody", "jsonBody(keyOrValue)", "Assert the JSON body has a property (string, dotted path ok) or deep-equals a value."),
                M("length", "length(n)", "Assert length equals n."),
                M("lengthOf", "lengthOf(n)", "Assert length equals n."),
                M("status", "status(code)", "Assert a numeric status property equals code."),
                M("contain", "contain(needle)", "Assert the value contains needle."),
                M("include", "include(needle)", "Assert the value includes needle."),
                M("match", "match(pattern)", "Assert the value matches the regex pattern."),
                M("above", "above(n)", "Assert value > n."),
                M("below", "below(n)", "Assert value < n."),
                M("least", "least(n)", "Assert value >= n."),
                M("most", "most(n)", "Assert value <= n."),
                M("throw", "throw()", "Assert the wrapped function throws."),
            }),

            new("_", "lodash-lite helpers.", new[]
            {
                M("get", "get(obj, path, default)", "Safe nested-property access."),
                M("set", "set(obj, path, value)", "Set a nested property (mutates in place)."),
                M("cloneDeep", "cloneDeep(obj)", "Deep clone via JSON round-trip."),
                M("isEmpty", "isEmpty(value)", "True for null/empty string/array/object."),
                M("pick", "pick(obj, keys)", "Return a copy with only the given keys."),
                M("omit", "omit(obj, keys)", "Return a copy without the given keys."),
                M("forEach", "forEach(collection, fn)", "Iterate an array or object."),
                M("map", "map(collection, fn)", "Map an array or object to a new array."),
                M("filter", "filter(collection, fn)", "Filter an array."),
            }),

            new("axios", "Minimal axios shim (delegates to bru.sendRequest).", new[]
            {
                M("request", "request(config)", "Send a request from a config object."),
                M("get", "get(url, config)", "Send a GET request."),
                M("post", "post(url, data, config)", "Send a POST request."),
                M("put", "put(url, data, config)", "Send a PUT request."),
                M("patch", "patch(url, data, config)", "Send a PATCH request."),
                M("delete", "delete(url, config)", "Send a DELETE request."),
            }),

            // Postman-compatible require() + injected globals (see JsModules). Functions with no
            // chainable surface, so no members — listed so autocomplete surfaces them.
            new("require", "Load a bundled library: lodash, moment, uuid, crypto-js, chai, tv4, ajv, cheerio, csv-parse/lib/sync, xml2js, atob, btoa, and Node core (url, querystring, util, path, buffer, assert, events).", Array.Empty<ScriptMember>()),
            new("xml2Json", "Parse an XML string into a JS object (xml2js shape: $ attrs, _ text, arrayed children).", Array.Empty<ScriptMember>()),
            new("atob", "Decode a base64 string to binary text.", Array.Empty<ScriptMember>()),
            new("btoa", "Base64-encode a binary string.", Array.Empty<ScriptMember>()),
        };

        return objects.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal);
    }

    private static ScriptMember M(string name, string sig, string desc) =>
        new(name, ScriptMemberKind.Method, sig, desc);
    private static ScriptMember Prop(string name, string sig, string desc) =>
        new(name, ScriptMemberKind.Property, sig, desc);
    private static ScriptMember Obj(string name, string sig, string desc, string returns) =>
        new(name, ScriptMemberKind.Object, sig, desc, returns);
}
