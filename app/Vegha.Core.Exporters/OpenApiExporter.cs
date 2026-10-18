using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Vegha.Core.Domain;

namespace Vegha.Core.Exporters;

/// <summary>
/// Projects a <see cref="Collection"/> onto an OpenAPI 3.0 document — roughly the inverse
/// of <c>OpenApiImporter</c>:
///   - The most common URL prefix (<c>{{baseUrl}}</c> or <c>scheme://host</c>) becomes
///     <c>servers[0]</c>; <c>{{var}}</c> references inside it become server variables.
///   - Each request becomes one operation (method + path); <c>:id</c> / <c>{{id}}</c> path
///     segments become <c>{id}</c> path parameters. Folder paths become tags.
///   - Query params, headers and bodies become parameters / requestBody with examples.
///   - Bearer / basic / digest / apikey / oauth2 auth becomes <c>securitySchemes</c>.
///
/// OpenAPI describes an API, not a test suite: scripts, tests and environments are dropped
/// (one warning per collection), as are gRPC / WebSocket requests.
/// </summary>
public static partial class OpenApiExporter
{
    public static string FileNameFor(Collection c, bool yaml) =>
        ExportNaming.Sanitize(c.Name) + (yaml ? ".openapi.yaml" : ".openapi.json");

    public static byte[] Export(Collection collection, ExportOptions options, ICollection<string> warnings)
    {
        var doc = BuildDocument(collection, warnings);
        var text = options.OpenApiYaml
            ? doc.SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0)
            : doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        return ExportNaming.ToUtf8(text);
    }

    public static OpenApiDocument BuildDocument(Collection collection, ICollection<string> warnings)
    {
        var ctx = new Context(collection, warnings);
        var flat = Flatten(collection).Where(x => ExportTree.IsHttpLike(x.Request)).ToList();

        foreach (var (r, _) in Flatten(collection).Where(x => !ExportTree.IsHttpLike(x.Request)))
            warnings.Add($"{collection.Name}: skipped {r.Kind} request \"{r.Name}\" (not representable in OpenAPI).");

        // Server = the most common URL prefix; everything else gets a path-level override.
        var primaryPrefix = flat
            .Select(x => SplitPrefix(x.Request.Url).Prefix)
            .Where(p => p.Length > 0)
            .GroupBy(p => p, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

        var doc = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = string.IsNullOrEmpty(collection.Name) ? "Exported collection" : collection.Name,
                Version = string.IsNullOrEmpty(collection.Version) ? "1.0" : collection.Version,
                Description = collection.Docs,
            },
            Servers = new List<OpenApiServer>(),
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents(),
            Tags = new List<OpenApiTag>(),
        };
        if (primaryPrefix.Length > 0) doc.Servers.Add(ctx.ServerFor(primaryPrefix));

        // Collection-level security — operations only override when they differ.
        var collectionScheme = ExportTree.IsConcrete(collection.Auth) ? ctx.SchemeFor(collection.Auth!, collection.Name) : null;
        if (collectionScheme is not null)
            doc.SecurityRequirements = new List<OpenApiSecurityRequirement> { collectionScheme.Value.Requirement };

        foreach (var (r, chain) in flat)
            AddOperation(ctx, doc, r, chain, primaryPrefix, collectionScheme?.Id);

        // Declare every tag used, with folder docs as the description.
        foreach (var (tag, folder) in ctx.Tags)
            doc.Tags.Add(new OpenApiTag { Name = tag, Description = string.IsNullOrEmpty(folder.Docs) ? null : folder.Docs });

        if (ctx.Schemes.Count > 0)
            doc.Components.SecuritySchemes = ctx.Schemes.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (ctx.FoldedIntoExamples > 0)
            warnings.Add($"{collection.Name}: {ctx.FoldedIntoExamples} request(s) share a method and path with another request and were added as request-body examples of that operation.");
        if (HasScriptsOrEnvs(collection))
            warnings.Add($"{collection.Name}: scripts, tests and environments are not part of OpenAPI and were left out.");

        return doc;
    }

    // ============================== Operations ==============================

    private static void AddOperation(Context ctx, OpenApiDocument doc, RequestItem r, IReadOnlyList<Folder> chain,
        string primaryPrefix, string? collectionSchemeId)
    {
        var method = r.Kind == RequestKind.GraphQL && string.IsNullOrEmpty(r.Method) ? "POST" : r.Method;
        if (!Enum.TryParse<OperationType>(method, ignoreCase: true, out var opType))
        {
            ctx.Warnings.Add($"{ctx.Collection.Name}: skipped \"{r.Name}\" — method {method} isn't an OpenAPI operation.");
            return;
        }

        var (prefix, rest) = SplitPrefix(r.Url);
        var (path, pathParamNames) = ToOpenApiPath(rest);

        // OpenAPI requires path templates to be unique regardless of parameter names
        // (/a/{id} and /a/{userId} collide). Reuse the first spelling and map this request's
        // path params onto its names by position.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var signature = PathParam().Replace(path, "{}");
        if (ctx.PathsBySignature.TryGetValue(signature, out var canonical))
        {
            var canonicalNames = PathParam().Matches(canonical).Select(m => m.Groups[1].Value).ToList();
            for (var i = 0; i < canonicalNames.Count && i < pathParamNames.Count; i++)
                aliases[canonicalNames[i]] = pathParamNames[i];
            path = canonical;
            pathParamNames = canonicalNames;
        }
        else
        {
            ctx.PathsBySignature[signature] = path;
        }

        if (!doc.Paths.TryGetValue(path, out var pathItem))
        {
            pathItem = new OpenApiPathItem();
            if (prefix.Length > 0 && !string.Equals(prefix, primaryPrefix, StringComparison.Ordinal))
                pathItem.Servers = new List<OpenApiServer> { ctx.ServerFor(prefix) };
            doc.Paths[path] = pathItem;
        }

        var body = BuildRequestBody(r, ctx.Collection, chain);
        if (pathItem.Operations.TryGetValue(opType, out var existing))
        {
            // Same method + path twice: keep the first operation, fold the second body in as
            // a named example when both carry the same media type.
            if (body is not null && existing.RequestBody is not null)
            {
                var (mime, media) = body.Content.First();
                if (existing.RequestBody.Content.TryGetValue(mime, out var existingMedia) && media.Example is not null)
                {
                    if (existingMedia.Example is not null)
                    {
                        existingMedia.Examples[existing.Summary ?? "default"] = new OpenApiExample { Value = existingMedia.Example };
                        existingMedia.Example = null;
                    }
                    existingMedia.Examples[r.Name] = new OpenApiExample { Summary = r.Name, Value = media.Example };
                    ctx.FoldedIntoExamples++;
                    return;
                }
            }
            ctx.Warnings.Add($"{ctx.Collection.Name}: \"{r.Name}\" duplicates {opType.ToString().ToUpperInvariant()} {path} and was skipped.");
            return;
        }

        var op = new OpenApiOperation
        {
            OperationId = ctx.UniqueOperationId(r.Name),
            Summary = r.Name,
            Description = string.IsNullOrEmpty(r.Docs) ? null : r.Docs,
            Parameters = BuildParameters(r, ctx.Collection, chain, pathParamNames, aliases),
            RequestBody = body,
            Responses = new OpenApiResponses { ["200"] = new OpenApiResponse { Description = "Successful response" } },
        };

        if (chain.Count > 0)
        {
            var tag = string.Join(" / ", chain.Select(f => f.Name));
            ctx.Tags.TryAdd(tag, chain[^1]);
            op.Tags = new List<OpenApiTag> { new() { Name = tag, Reference = new OpenApiReference { Type = ReferenceType.Tag, Id = tag } } };
        }

        // Security override only when the effective auth differs from the collection's.
        var auth = ExportTree.EffectiveAuth(ctx.Collection, chain, r);
        if (auth is not null && auth.Type != AuthType.None)
        {
            var scheme = ctx.SchemeFor(auth, r.Name);
            if (scheme is not null && scheme.Value.Id != collectionSchemeId)
                op.Security = new List<OpenApiSecurityRequirement> { scheme.Value.Requirement };
        }

        pathItem.Operations[opType] = op;
    }

    private static List<OpenApiParameter> BuildParameters(RequestItem r, Collection c, IReadOnlyList<Folder> chain,
        IReadOnlyList<string> pathParamNames, IReadOnlyDictionary<string, string> aliases)
    {
        var list = new List<OpenApiParameter>();

        foreach (var name in pathParamNames)
        {
            // The request may spell the parameter differently when its path was merged
            // into an existing template (see AddOperation).
            var own = aliases.TryGetValue(name, out var alias) ? alias : name;
            var declared = r.PathParams.FirstOrDefault(p => p.Name == own);
            list.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Path,
                Required = true,
                Description = declared?.Description,
                Schema = new OpenApiSchema { Type = "string" },
                Example = string.IsNullOrEmpty(declared?.Value) ? null : new OpenApiString(declared!.Value),
            });
        }

        foreach (var q in r.Params.Where(p => !string.IsNullOrEmpty(p.Name)))
        {
            if (list.Any(p => p.In == ParameterLocation.Query && p.Name == q.Name)) continue;
            list.Add(new OpenApiParameter
            {
                Name = q.Name,
                In = ParameterLocation.Query,
                Required = false,
                Description = q.Description,
                Schema = new OpenApiSchema { Type = "string" },
                Example = string.IsNullOrEmpty(q.Value) ? null : new OpenApiString(q.Value),
            });
        }

        // Content-Type is expressed by requestBody and Authorization by security schemes.
        foreach (var h in ExportTree.EffectiveHeaders(c, chain, r).Where(h => h.Enabled && !string.IsNullOrEmpty(h.Name)))
        {
            if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new OpenApiParameter
            {
                Name = h.Name,
                In = ParameterLocation.Header,
                Required = false,
                Description = h.Description,
                Schema = new OpenApiSchema { Type = "string" },
                Example = string.IsNullOrEmpty(h.Value) ? null : new OpenApiString(h.Value),
            });
        }
        return list;
    }

    private static OpenApiRequestBody? BuildRequestBody(RequestItem r, Collection c, IReadOnlyList<Folder> chain)
    {
        var b = r.Body;
        var contentTypeHeader = ExportTree.EffectiveHeaders(c, chain, r)
            .FirstOrDefault(h => h.Enabled && h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        string Mime(string fallback) =>
            !string.IsNullOrWhiteSpace(contentTypeHeader) && !contentTypeHeader.Contains("{{", StringComparison.Ordinal)
                ? contentTypeHeader.Split(';')[0].Trim()
                : fallback;

        OpenApiRequestBody Single(string mime, OpenApiMediaType media) => new()
        {
            Content = new Dictionary<string, OpenApiMediaType> { [mime] = media },
        };

        switch (b.Mode)
        {
            case BodyMode.Json:
            {
                var node = ParseJsonLenient(b.Content);
                return Single(Mime("application/json"), node is null
                    ? new OpenApiMediaType { Schema = new OpenApiSchema { Type = "object" }, Example = new OpenApiString(b.Content ?? string.Empty) }
                    : new OpenApiMediaType { Schema = InferSchema(node), Example = ToAny(node) });
            }
            case BodyMode.Xml:
                return Single(Mime("application/xml"), new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = "string" },
                    Example = new OpenApiString(b.Content ?? string.Empty),
                });
            case BodyMode.Text:
            case BodyMode.Sparql:
                return Single(Mime(b.Mode == BodyMode.Sparql ? "application/sparql-query" : "text/plain"), new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = "string" },
                    Example = new OpenApiString(b.Content ?? string.Empty),
                });
            case BodyMode.FormUrlEncoded:
                return Single("application/x-www-form-urlencoded", new OpenApiMediaType
                {
                    Schema = FormSchema(b.FormData.Where(p => p.Enabled).Select(p => (p.Name, p.Value, false, p.Description))),
                });
            case BodyMode.MultipartForm:
            {
                var fields = b.MultipartItems.Count > 0
                    ? b.MultipartItems.Where(p => p.Enabled)
                        .Select(p => (p.Name, p.Value, string.Equals(p.Kind, "file", StringComparison.OrdinalIgnoreCase), p.Description))
                    : b.FormData.Where(p => p.Enabled).Select(p => (p.Name, p.Value, false, p.Description));
                return Single("multipart/form-data", new OpenApiMediaType { Schema = FormSchema(fields) });
            }
            case BodyMode.GraphQL:
            {
                var payload = new JsonObject
                {
                    ["query"] = b.GraphQLQuery ?? string.Empty,
                    ["variables"] = ParseJsonLenient(b.GraphQLVariables) ?? new JsonObject(),
                };
                return Single("application/json", new OpenApiMediaType { Schema = InferSchema(payload), Example = ToAny(payload) });
            }
            case BodyMode.Binary:
                return Single(string.IsNullOrEmpty(b.FileContentType) ? "application/octet-stream" : b.FileContentType!,
                    new OpenApiMediaType { Schema = new OpenApiSchema { Type = "string", Format = "binary" } });
            default:
                return null;
        }
    }

    private static OpenApiSchema FormSchema(IEnumerable<(string Name, string Value, bool IsFile, string? Description)> fields)
    {
        var schema = new OpenApiSchema { Type = "object", Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal) };
        foreach (var (name, value, isFile, description) in fields)
        {
            if (string.IsNullOrEmpty(name) || schema.Properties.ContainsKey(name)) continue;
            schema.Properties[name] = isFile
                ? new OpenApiSchema { Type = "string", Format = "binary", Description = description }
                : new OpenApiSchema { Type = "string", Description = description, Example = string.IsNullOrEmpty(value) ? null : new OpenApiString(value) };
        }
        return schema;
    }

    // ============================== URL → server + path ==============================

    /// <summary>Splits a request URL into its server prefix (<c>{{var}}</c> or
    /// <c>scheme://host[:port]</c>) and the remaining path + query.</summary>
    internal static (string Prefix, string Remainder) SplitPrefix(string? url)
    {
        url ??= string.Empty;
        var m = LeadingVar().Match(url);
        if (m.Success) return (m.Value, url[m.Length..]);
        m = SchemeHost().Match(url);
        if (m.Success) return (m.Value, url[m.Length..]);
        return (string.Empty, url);
    }

    /// <summary>Normalizes the path part: drops query/fragment, ensures a leading '/', and
    /// rewrites <c>:name</c> segments and <c>{{name}}</c> references to <c>{name}</c>.</summary>
    internal static (string Path, List<string> ParamNames) ToOpenApiPath(string rest)
    {
        var path = ExportTree.StripQuery(rest);
        if (!path.StartsWith('/')) path = "/" + path;
        var names = new List<string>();

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.StartsWith(':') && s.Length > 1) s = "{" + s[1..] + "}";
            s = PathVar().Replace(s, mm => "{" + mm.Groups[1].Value.Trim() + "}");
            foreach (Match pm in PathParam().Matches(s))
                if (!names.Contains(pm.Groups[1].Value)) names.Add(pm.Groups[1].Value);
            segments[i] = s;
        }
        path = string.Join('/', segments);
        if (path.Length > 1) path = path.TrimEnd('/');
        return (path, names);
    }

    [GeneratedRegex(@"^\{\{[^{}]+\}\}")]
    private static partial Regex LeadingVar();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://[^/?#]+")]
    private static partial Regex SchemeHost();

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PathVar();

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PathParam();

    // ============================== Context ==============================

    private sealed class Context(Collection collection, ICollection<string> warnings)
    {
        private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _schemeIdsBySignature = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedAuth = new(StringComparer.Ordinal);

        public Collection Collection { get; } = collection;
        public ICollection<string> Warnings { get; } = warnings;
        public Dictionary<string, Folder> Tags { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OpenApiSecurityScheme> Schemes { get; } = new(StringComparer.Ordinal);
        /// <summary>Path template with parameter names blanked (<c>/a/{}</c>) → the first
        /// spelling seen, so differently-named templates share one path item.</summary>
        public Dictionary<string, string> PathsBySignature { get; } = new(StringComparer.Ordinal);
        public int FoldedIntoExamples { get; set; }

        public string UniqueOperationId(string name) =>
            ExportNaming.Unique(_operationIds, string.IsNullOrWhiteSpace(name) ? "operation" : name.Trim());

        /// <summary>Server for a prefix. <c>{{var}}</c> references resolve through collection
        /// vars, then environments; each one becomes an OpenAPI server variable whose default
        /// is that value, so the importer reconstructs the same baseUrl.</summary>
        public OpenApiServer ServerFor(string prefix)
        {
            var server = new OpenApiServer { Variables = new Dictionary<string, OpenApiServerVariable>() };
            var whole = LeadingVar().Match(prefix);
            var url = prefix;
            if (whole.Success && whole.Length == prefix.Length)
            {
                // {{baseUrl}} → substitute its value (which may itself reference vars).
                var name = prefix[2..^2].Trim();
                var value = Resolve(name);
                url = string.IsNullOrEmpty(value) ? "{" + name + "}" : value!;
                if (string.IsNullOrEmpty(value))
                    server.Variables[name] = new OpenApiServerVariable { Default = "http://localhost" };
            }
            server.Url = PathVar().Replace(url, m =>
            {
                var name = m.Groups[1].Value;
                if (!server.Variables.ContainsKey(name))
                    server.Variables[name] = new OpenApiServerVariable
                    {
                        // OpenAPI requires a non-empty default; the name is a readable placeholder.
                        Default = Resolve(name) ?? name,
                        Description = Resolve(name) is null ? "Set this to the value of {{" + name + "}}." : null,
                    };
                return "{" + name + "}";
            });
            return server;
        }

        private string? Resolve(string name)
        {
            var v = Collection.Variables.FirstOrDefault(x => x.Name == name && x.Enabled)?.Value;
            if (!string.IsNullOrEmpty(v)) return v;
            foreach (var env in Collection.Environments)
            {
                v = env.Variables.FirstOrDefault(x => x.Name == name && x.Enabled)?.Value;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        /// <summary>Registers (once per distinct configuration) and returns the security
        /// scheme for <paramref name="auth"/>; null + a warning for unsupported types.</summary>
        public (string Id, OpenApiSecurityRequirement Requirement)? SchemeFor(AuthConfig auth, string ownerName)
        {
            var p = auth.Parameters;
            string Get(string k) => p.TryGetValue(k, out var v) ? v : string.Empty;

            OpenApiSecurityScheme? scheme;
            string baseId;
            var scopes = new List<string>();
            switch (auth.Type)
            {
                case AuthType.Bearer:
                    baseId = "bearerAuth";
                    scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" };
                    break;
                case AuthType.Basic:
                    baseId = "basicAuth";
                    scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "basic" };
                    break;
                case AuthType.Digest:
                    baseId = "digestAuth";
                    scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "digest" };
                    break;
                case AuthType.ApiKey:
                    baseId = "apiKeyAuth";
                    scheme = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        Name = string.IsNullOrEmpty(Get("key")) ? "X-API-Key" : Get("key"),
                        In = Get("placement") == "queryparams" ? ParameterLocation.Query : ParameterLocation.Header,
                    };
                    break;
                case AuthType.OAuth2:
                {
                    baseId = "oauth2Auth";
                    scopes = Get("scope").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    var flow = new OpenApiOAuthFlow
                    {
                        TokenUrl = ToUri(Get("access_token_url")),
                        AuthorizationUrl = ToUri(Get("authorization_url")),
                        Scopes = scopes.ToDictionary(s => s, _ => string.Empty),
                    };
                    var flows = new OpenApiOAuthFlows();
                    switch (Get("grant_type"))
                    {
                        case "authorization_code": flows.AuthorizationCode = flow; break;
                        case "password": flow.AuthorizationUrl = null; flows.Password = flow; break;
                        case "implicit": flow.TokenUrl = null; flows.Implicit = flow; break;
                        default: flow.AuthorizationUrl = null; flows.ClientCredentials = flow; break;
                    }
                    scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.OAuth2, Flows = flows };
                    break;
                }
                default:
                    if (_warnedAuth.Add(auth.Type.ToString()))
                        Warnings.Add($"{Collection.Name}: {auth.Type} auth (first seen on \"{ownerName}\") has no OpenAPI security scheme and was left out.");
                    return null;
            }

            var signature = $"{auth.Type}|{scheme.Name}|{scheme.In}|{Get("grant_type")}|{Get("access_token_url")}|{Get("scope")}";
            if (!_schemeIdsBySignature.TryGetValue(signature, out var id))
            {
                var used = new HashSet<string>(Schemes.Keys, StringComparer.Ordinal);
                id = ExportNaming.Unique(used, baseId);
                scheme.Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = id };
                Schemes[id] = scheme;
                _schemeIdsBySignature[signature] = id;
            }

            var requirement = new OpenApiSecurityRequirement { [Schemes[id]] = scopes };
            return (id, requirement);
        }

        private static Uri? ToUri(string value) =>
            !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri) ? uri : null;
    }

    // ============================== JSON → schema / example ==============================

    /// <summary>Parses a JSON body, tolerating unquoted <c>{{var}}</c> placeholders (common in
    /// Vegha bodies, e.g. <c>"id": {{userId}}</c>) by quoting them first.</summary>
    private static JsonNode? ParseJsonLenient(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try { return JsonNode.Parse(content); }
        catch (JsonException) { /* retry with quoted placeholders */ }
        try { return JsonNode.Parse(UnquotedVar().Replace(content, m => "\"" + m.Value + "\"")); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"(?<![""\w])\{\{[^{}""]+\}\}(?![""\w])")]
    private static partial Regex UnquotedVar();

    private static OpenApiSchema InferSchema(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return new OpenApiSchema
                {
                    Type = "object",
                    Properties = obj.ToDictionary(kv => kv.Key, kv => InferSchema(kv.Value), StringComparer.Ordinal),
                };
            case JsonArray arr:
                return new OpenApiSchema { Type = "array", Items = arr.Count > 0 ? InferSchema(arr[0]) : new OpenApiSchema() };
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    JsonValueKind.String => new OpenApiSchema { Type = "string" },
                    JsonValueKind.True or JsonValueKind.False => new OpenApiSchema { Type = "boolean" },
                    JsonValueKind.Number => v.TryGetValue<long>(out _) ? new OpenApiSchema { Type = "integer" } : new OpenApiSchema { Type = "number" },
                    _ => new OpenApiSchema { Nullable = true },
                };
            default:
                return new OpenApiSchema { Nullable = true };
        }
    }

    private static IOpenApiAny ToAny(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var o = new OpenApiObject();
                foreach (var (k, v) in obj) o[k] = ToAny(v);
                return o;
            }
            case JsonArray arr:
            {
                var a = new OpenApiArray();
                a.AddRange(arr.Select(ToAny));
                return a;
            }
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String: return new OpenApiString(v.GetValue<string>());
                    case JsonValueKind.True: return new OpenApiBoolean(true);
                    case JsonValueKind.False: return new OpenApiBoolean(false);
                    case JsonValueKind.Number:
                        if (v.TryGetValue<long>(out var l)) return new OpenApiLong(l);
                        return new OpenApiDouble(v.GetValue<double>());
                    default: return new OpenApiNull();
                }
            default:
                return new OpenApiNull();
        }
    }

    // ============================== tree helpers ==============================

    private static IEnumerable<(RequestItem Request, IReadOnlyList<Folder> Chain)> Flatten(Collection c)
    {
        foreach (var r in c.Requests.OrderBy(r => r.Sequence)) yield return (r, Array.Empty<Folder>());
        foreach (var x in FlattenFolders(c.Folders, new List<Folder>())) yield return x;
    }

    private static IEnumerable<(RequestItem, IReadOnlyList<Folder>)> FlattenFolders(IList<Folder> folders, List<Folder> chain)
    {
        foreach (var f in folders)
        {
            var next = new List<Folder>(chain) { f };
            foreach (var r in f.Requests.OrderBy(r => r.Sequence)) yield return (r, next);
            foreach (var x in FlattenFolders(f.Folders, next)) yield return x;
        }
    }

    private static bool HasScriptsOrEnvs(Collection c)
    {
        static bool Any(params string?[] s) => s.Any(x => !string.IsNullOrWhiteSpace(x));
        bool Walk(IList<RequestItem> reqs, IList<Folder> folders) =>
            reqs.Any(r => Any(r.PreRequestScript, r.PostResponseScript, r.Tests))
            || folders.Any(f => Any(f.PreRequestScript, f.PostResponseScript, f.TestsScript) || Walk(f.Requests, f.Folders));
        return c.Environments.Count > 0
            || Any(c.PreRequestScript, c.PostResponseScript, c.TestsScript)
            || Walk(c.Requests, c.Folders);
    }
}
