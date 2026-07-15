using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Codegen;

/// <summary>Shared, language-neutral resolution of a RequestItem with placeholder interpolation.</summary>
internal sealed record CodegenContext(
    string Method,
    string Url,
    IReadOnlyList<KvPair> Headers,
    IReadOnlyList<KvPair> QueryParams,
    string? Body,
    string? ContentType)
{
    /// <summary>Headers plus an inferred Content-Type row when the body implies one and the
    /// user didn't set the header explicitly. Most emitters render this list verbatim.</summary>
    public List<KvPair> HeadersWithContentType()
    {
        var list = Headers.ToList();
        if (ContentType is not null && !list.Any(h =>
                string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new KvPair("Content-Type", ContentType, true));
        }
        return list;
    }

    public static CodegenContext From(RequestItem r, IReadOnlyDictionary<string, string>? vars)
    {
        string Resolve(string s) => vars is null ? s : Interpolator.Resolve(s, vars);

        var url = AppendQuery(Resolve(r.Url), r.Params, vars);

        var headers = r.Headers
            .Where(h => h.Enabled && !string.IsNullOrEmpty(h.Name))
            .Select(h => new KvPair(Resolve(h.Name), Resolve(h.Value), true))
            .ToList();

        // Auth shorthand: surface bearer/basic/apikey-header into Authorization header for snippets,
        // so generated code includes the header rather than requiring users to wire auth themselves.
        if (r.Auth is { } a)
        {
            switch (a.Type)
            {
                case AuthType.Bearer when a.Parameters.TryGetValue("token", out var t):
                    headers.Add(new KvPair("Authorization", "Bearer " + Resolve(t), true));
                    break;
                case AuthType.Basic when a.Parameters.TryGetValue("username", out var u)
                                          && a.Parameters.TryGetValue("password", out var p):
                    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                        $"{Resolve(u)}:{Resolve(p)}"));
                    headers.Add(new KvPair("Authorization", "Basic " + encoded, true));
                    break;
                case AuthType.ApiKey
                    when a.Parameters.TryGetValue("key", out var k)
                      && a.Parameters.TryGetValue("value", out var v):
                    var placement = a.Parameters.TryGetValue("placement", out var pl) ? pl : "header";
                    if (placement != "queryparams")
                        headers.Add(new KvPair(Resolve(k), Resolve(v), true));
                    break;
            }
        }

        var (body, contentType) = ResolveBody(r.Body, vars);
        return new CodegenContext(
            r.Method.ToUpperInvariant(), url, headers,
            r.Params.Where(p => p.Enabled).ToList(), body, contentType);
    }

    private static string AppendQuery(string url, IList<KvPair> queryParams, IReadOnlyDictionary<string, string>? vars)
    {
        var enabled = queryParams.Where(p => p.Enabled && !string.IsNullOrEmpty(p.Name)).ToList();
        if (enabled.Count == 0) return url;
        var sep = url.Contains('?') ? "&" : "?";
        var parts = enabled.Select(p =>
        {
            var name = vars is null ? p.Name : Interpolator.Resolve(p.Name, vars);
            var value = vars is null ? p.Value : Interpolator.Resolve(p.Value, vars);
            return $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        });
        return url + sep + string.Join("&", parts);
    }

    private static (string? Body, string? ContentType) ResolveBody(BodyConfig body, IReadOnlyDictionary<string, string>? vars)
    {
        string Resolve(string s) => vars is null ? s : Interpolator.Resolve(s, vars);
        return body.Mode switch
        {
            BodyMode.None => (null, null),
            BodyMode.Json => (string.IsNullOrEmpty(body.Content) ? null : Resolve(body.Content), "application/json"),
            BodyMode.Text => (string.IsNullOrEmpty(body.Content) ? null : Resolve(body.Content), "text/plain"),
            BodyMode.Xml  => (string.IsNullOrEmpty(body.Content) ? null : Resolve(body.Content), "application/xml"),
            BodyMode.GraphQL => (BuildGraphQLBody(body, Resolve), "application/json"),
            BodyMode.FormUrlEncoded => (BuildFormBody(body.FormData, Resolve), "application/x-www-form-urlencoded"),
            _ => (null, null)
        };
    }

    private static string BuildGraphQLBody(BodyConfig body, Func<string, string> resolve)
    {
        var query = body.GraphQLQuery is null ? string.Empty : resolve(body.GraphQLQuery);
        var rawVars = body.GraphQLVariables is null ? "{}" : resolve(body.GraphQLVariables);
        var trimmed = rawVars.TrimStart();
        var vars = (trimmed.StartsWith('{') || trimmed.StartsWith('[')) ? rawVars : "{}";
        // First named operation for multi-op documents — keeps generated code's wire body
        // identical to what the editor's Send produces.
        var opName = Vegha.Core.GraphQL.GraphQLDocumentAnalyzer.ResolveOperationNameForSend(query);
        return "{\"query\":" + System.Text.Json.JsonSerializer.Serialize(query) +
            (opName is null
                ? string.Empty
                : ",\"operationName\":" + System.Text.Json.JsonSerializer.Serialize(opName)) +
            ",\"variables\":" + vars + "}";
    }

    private static string BuildFormBody(IList<KvPair> fields, Func<string, string> resolve)
    {
        var pairs = fields.Where(p => p.Enabled && !string.IsNullOrEmpty(p.Name))
            .Select(p => $"{Uri.EscapeDataString(resolve(p.Name))}={Uri.EscapeDataString(resolve(p.Value))}");
        return string.Join("&", pairs);
    }
}
