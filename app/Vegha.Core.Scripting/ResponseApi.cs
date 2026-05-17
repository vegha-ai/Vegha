using System.Text.Json;

namespace Vegha.Core.Scripting;

/// <summary>The <c>res</c> object exposed to post-response scripts. Mirrors Bruno's
/// <c>bruno-response.js</c> + Postman's <c>pm.response.*</c>: status / statusText / headers
/// / responseTime / url / size, plus a <c>headerList</c> PropertyList facade and a
/// <see cref="getBody"/> that returns the parsed value when the body is JSON.</summary>
public sealed class ResponseApi
{
    private readonly Dictionary<string, string> _headers;
    private readonly object? _parsedBody;
    private readonly long _bodyByteCount;
    private readonly long _headerByteCount;

    public int status { get; }
    public string statusText { get; }
    public string body { get; }
    public long responseTime { get; }
    public string url { get; }

    /// <summary>Read-only PropertyList facade — Bruno's <c>res.headerList.*</c>.</summary>
    public PropertyListApi headerList { get; }

    public ResponseApi(int status, string statusText, string body, long responseTime,
        IEnumerable<KeyValuePair<string, string>> headers, string? url = null)
    {
        this.status = status;
        this.statusText = statusText;
        this.body = body;
        this.responseTime = responseTime;
        this.url = url ?? string.Empty;

        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) _headers[k] = v;

        _parsedBody = TryParseJsonByContentType(_headers, body);
        _bodyByteCount = body is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(body);
        _headerByteCount = _headers.Sum(kv =>
            System.Text.Encoding.UTF8.GetByteCount(kv.Key) +
            System.Text.Encoding.UTF8.GetByteCount(kv.Value) + 4 /* ": " + CRLF */);

        // Read-only facade — response is already over the wire.
        headerList = new PropertyListApi(_headers, readOnly: true);
    }

    // ---- accessors (Bruno + Postman parity) ----

    public int getStatus() => status;
    public string getStatusText() => statusText;

    /// <summary>Returns the parsed body when Content-Type is JSON, otherwise the raw string.
    /// Postman parity: <c>pm.response.json()</c> maps here.</summary>
    public object? getBody() => _parsedBody ?? body;

    /// <summary>Always returns the raw response body as a string, regardless of Content-Type.
    /// Bruno's translation of <c>pm.response.text()</c>.</summary>
    public string getBodyAsText() => body ?? string.Empty;

    public Dictionary<string, string> getHeaders() => new(_headers, StringComparer.OrdinalIgnoreCase);

    public string? getHeader(string name) =>
        _headers.TryGetValue(name, out var v) ? v : null;

    public bool hasHeader(string name) => _headers.ContainsKey(name);

    public long getResponseTime() => responseTime;

    public string getUrl() => url;

    /// <summary>Returns <c>{ header, body, total }</c> byte sizes — Postman parity
    /// (<c>pm.response.size()</c>).</summary>
    public ResponseSize getSize() => new(_headerByteCount, _bodyByteCount, _headerByteCount + _bodyByteCount);

    // ---- helpers ----

    /// <summary>Returns a JS-friendly object graph (nested Dictionary&lt;string,object?&gt;
    /// + List&lt;object?&gt; + primitives) when the body is JSON. Null when the body is empty,
    /// the Content-Type doesn't look like JSON, or parsing fails — callers fall back to the
    /// raw <see cref="body"/> string.</summary>
    private static object? TryParseJsonByContentType(IDictionary<string, string> headers, string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        if (!headers.TryGetValue("Content-Type", out var ct)) return null;
        if (!ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ToObject(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static object? ToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject()) obj[p.Name] = ToObject(p.Value);
                return obj;
            case JsonValueKind.Array:
                var arr = new List<object?>();
                foreach (var item in el.EnumerateArray()) arr.Add(ToObject(item));
                return arr;
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            case JsonValueKind.True:  return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null:  return null;
            default: return null;
        }
    }
}

/// <summary>Three-byte-count payload returned by <see cref="ResponseApi.getSize"/>.
/// Postman parity: <c>{ header: number, body: number, total: number }</c>.</summary>
public sealed record ResponseSize(long header, long body, long total);
