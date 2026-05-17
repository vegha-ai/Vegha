namespace Vegha.Core.Scripting;

/// <summary>
/// The <c>req</c> object exposed to pre-request and post-response scripts — a mutable proxy
/// over the outgoing request. Mutations made by a pre-request script (URL change, header
/// insert, body rewrite) are read back by the host before the request is sent.
///
/// Mirrors the surface from <c>bruno-js/src/bruno-request.js</c> + Postman's
/// <c>pm.request.*</c>. Includes URL projection helpers (getName / getHost / getPath /
/// getQueryString / getPathParams) that Postman-translated scripts expect, plus a
/// <see cref="headerList"/> facade for <c>add</c>/<c>remove</c>/<c>upsert</c>/<c>each</c>/
/// <c>filter</c>/<c>map</c> patterns.
/// </summary>
public sealed class RequestApi
{
    private readonly Dictionary<string, string> _headers;
    private readonly Dictionary<string, string> _pathParams;

    public RequestApi(string method, string url, string? body,
        IEnumerable<KeyValuePair<string, string>> headers,
        string? name = null,
        IEnumerable<KeyValuePair<string, string>>? pathParams = null)
    {
        Method = method;
        Url = url;
        Body = body;
        Name = name ?? string.Empty;
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) _headers[k] = v;
        _pathParams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pathParams is not null)
            foreach (var (k, v) in pathParams) _pathParams[k] = v;

        headerList = new PropertyListApi(_headers);
    }

    public string Method { get; private set; }
    public string Url { get; private set; }
    public string? Body { get; private set; }
    public string Name { get; }

    /// <summary>Snapshot of the current header set after script mutations.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    /// <summary>Snapshot of path params at script-invocation time.</summary>
    public IReadOnlyDictionary<string, string> PathParams => _pathParams;

    // ---- Method / URL / body ----

    public string getMethod() => Method;
    public void setMethod(string method) => Method = method ?? string.Empty;

    public string getUrl() => Url;
    public void setUrl(string url) => Url = url ?? string.Empty;

    public string? getBody() => Body;
    public void setBody(string? body) => Body = body;

    /// <summary>Request name (Bruno's <c>req.getName()</c>). Useful for logging and for
    /// chained-request flows that branch on the originating request.</summary>
    public string getName() => Name;

    // ---- URL projections (Bruno + Postman parity) ----

    /// <summary>Returns the host portion of the URL (no scheme, no path, no port).
    /// Empty string when URL doesn't parse — caller scripts get a benign empty string
    /// instead of a thrown exception.</summary>
    public string getHost() => TryUri(out var u) ? u!.Host : string.Empty;

    /// <summary>Returns the path portion of the URL (e.g. <c>/users/42</c>). Empty
    /// when URL doesn't parse.</summary>
    public string getPath() => TryUri(out var u) ? u!.AbsolutePath : string.Empty;

    /// <summary>Returns the query string portion of the URL (without the leading "?").
    /// Empty when the URL has no query or doesn't parse.</summary>
    public string getQueryString()
    {
        if (!TryUri(out var u)) return string.Empty;
        var q = u!.Query;
        return q.StartsWith("?") ? q[1..] : q;
    }

    /// <summary>Returns the dictionary of path params declared on the request
    /// (Bruno <c>req.getPathParams()</c>).</summary>
    public Dictionary<string, string> getPathParams() => new(_pathParams, StringComparer.Ordinal);

    // ---- Headers ----

    public string? getHeader(string name) =>
        _headers.TryGetValue(name, out var v) ? v : null;

    public void setHeader(string name, string value) =>
        _headers[name] = value ?? string.Empty;

    public void removeHeader(string name) => _headers.Remove(name);

    public Dictionary<string, string> getHeaders() =>
        new(_headers, StringComparer.OrdinalIgnoreCase);

    /// <summary>Postman/Bruno PropertyList facade over headers. Mutations are live —
    /// they flow into <see cref="Headers"/> immediately and are read by the executor on send.</summary>
    public PropertyListApi headerList { get; }

    // ---- helpers ----

    private bool TryUri(out Uri? uri)
    {
        // Try absolute first; fall back to building a fake absolute when the URL is a
        // template like "/users/{{id}}" so getHost/getPath still produce something useful.
        if (Uri.TryCreate(Url, UriKind.Absolute, out uri)) return true;
        if (Uri.TryCreate("http://placeholder" + (Url.StartsWith("/") ? Url : "/" + Url),
            UriKind.Absolute, out uri))
        {
            return true;
        }
        uri = null;
        return false;
    }
}
