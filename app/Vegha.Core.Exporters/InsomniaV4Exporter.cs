using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vegha.Core.Domain;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.Exporters;

/// <summary>
/// Emits an Insomnia v4 export (<c>_type: export</c>, <c>__export_format: 4</c>): a flat
/// <c>resources[]</c> list linked by <c>parentId</c>, the shape <c>InsomniaImporter.ParseV4</c>
/// reads. One <c>workspace</c> resource per collection; collection variables become the base
/// environment and each Vegha environment a sub-environment.
///
/// Insomnia v4 has no collection-level headers/auth, so every request carries its
/// effective (inherited) headers and auth. Scripts are not exported (different API).
/// </summary>
public static partial class InsomniaV4Exporter
{
    public static string FileNameFor(Collection c) => ExportNaming.Sanitize(c.Name) + ".insomnia.json";

    /// <param name="extraEnvironments">Workspace-level environments to add as further
    /// sub-environments (skipped when a collection env has the same name).</param>
    public static byte[] Export(
        Collection collection, ExportOptions options, ICollection<string> warnings,
        IEnumerable<DomainEnv>? extraEnvironments = null)
    {
        var resources = new JsonArray();
        var ids = new IdFactory(collection.Name);
        var ctx = new Context(collection, warnings, resources, ids);

        var workspaceId = ids.Next("wrk", "workspace");
        resources.Add(new JsonObject
        {
            ["_id"] = workspaceId,
            ["_type"] = "workspace",
            ["parentId"] = null,
            ["name"] = collection.Name,
            ["description"] = collection.Docs ?? string.Empty,
            ["scope"] = "collection",
        });

        // Base environment = collection vars; sub-environments = Vegha envs.
        var baseEnvId = ids.Next("env", "base");
        resources.Add(new JsonObject
        {
            ["_id"] = baseEnvId,
            ["_type"] = "environment",
            ["parentId"] = workspaceId,
            ["name"] = "Base Environment",
            ["data"] = EnvData(collection.Variables),
            ["isPrivate"] = false,
            ["metaSortKey"] = 0,
        });

        if (options.IncludeEnvironments)
        {
            var envs = collection.Environments.ToList();
            if (extraEnvironments is not null)
            {
                foreach (var extra in extraEnvironments)
                    if (!envs.Any(e => string.Equals(e.Name, extra.Name, StringComparison.OrdinalIgnoreCase)))
                        envs.Add(extra);
            }
            var sort = 1;
            foreach (var env in envs)
            {
                var redacted = EnvironmentExportHelper.Redact(env, options.IncludeSecrets);
                var node = new JsonObject
                {
                    ["_id"] = ids.Next("env", "env/" + env.Name),
                    ["_type"] = "environment",
                    ["parentId"] = baseEnvId,
                    ["name"] = env.Name,
                    ["data"] = EnvData(redacted.Variables),
                    ["isPrivate"] = false,
                    ["metaSortKey"] = sort++,
                };
                if (!string.IsNullOrEmpty(env.Color)) node["color"] = env.Color;
                resources.Add(node);
            }
        }

        WalkFolder(ctx, workspaceId, "", collection.Requests, collection.Folders, new List<Folder>());

        if (HasScripts(collection))
            warnings.Add($"{collection.Name}: scripts and tests are not included in Insomnia exports.");

        var root = new JsonObject
        {
            ["_type"] = "export",
            ["__export_format"] = 4,
            ["__export_date"] = DateTime.UtcNow.ToString("O"),
            ["__export_source"] = "vegha",
            ["resources"] = resources,
        };
        return ExportNaming.ToUtf8(root);
    }

    private sealed record Context(Collection Collection, ICollection<string> Warnings, JsonArray Resources, IdFactory Ids);

    private static void WalkFolder(Context ctx, string parentId, string path,
        IList<RequestItem> requests, IList<Folder> folders, List<Folder> chain)
    {
        var sort = 0;
        foreach (var folder in folders)
        {
            var folderPath = path + "/" + folder.Name;
            var id = ctx.Ids.Next("fld", folderPath);
            var node = new JsonObject
            {
                ["_id"] = id,
                ["_type"] = "request_group",
                ["parentId"] = parentId,
                ["name"] = folder.Name,
                ["description"] = folder.Docs ?? string.Empty,
                ["environment"] = EnvData(folder.Variables),
                ["metaSortKey"] = sort++,
            };
            ctx.Resources.Add(node);
            chain.Add(folder);
            WalkFolder(ctx, id, folderPath, folder.Requests, folder.Folders, chain);
            chain.RemoveAt(chain.Count - 1);
        }

        foreach (var r in requests.OrderBy(r => r.Sequence))
        {
            if (!ExportTree.IsHttpLike(r))
            {
                ctx.Warnings.Add($"{ctx.Collection.Name}: skipped {r.Kind} request \"{r.Name}\" (not representable in Insomnia v4).");
                continue;
            }
            ctx.Resources.Add(EmitRequest(ctx, r, parentId, path + "/" + r.Name, chain, sort++));
        }
    }

    private static JsonObject EmitRequest(Context ctx, RequestItem r, string parentId, string path, IReadOnlyList<Folder> chain, int sort)
    {
        // Insomnia keeps query params separately — strip them from the URL so they aren't doubled.
        var url = r.Params.Count > 0 ? ExportTree.StripQuery(r.Url ?? string.Empty) : r.Url ?? string.Empty;
        var auth = ExportTree.EffectiveAuth(ctx.Collection, chain, r);

        return new JsonObject
        {
            ["_id"] = ctx.Ids.Next("req", path),
            ["_type"] = "request",
            ["parentId"] = parentId,
            ["name"] = r.Name,
            ["description"] = r.Docs ?? string.Empty,
            ["method"] = string.IsNullOrEmpty(r.Method) ? "GET" : r.Method.ToUpperInvariant(),
            ["url"] = Nunjucks(ApplyPathParams(url, r.PathParams)),
            ["headers"] = KvArray(ExportTree.EffectiveHeaders(ctx.Collection, chain, r)),
            ["parameters"] = KvArray(r.Params),
            ["body"] = EmitBody(r.Body),
            ["authentication"] = EmitAuth(ctx, auth, r.Name),
            ["metaSortKey"] = sort,
        };
    }

    /// <summary>Insomnia has no <c>:param</c> path variables — substitute each declared path
    /// param's value (or a <c>{{ _.name }}</c> reference when it's empty).</summary>
    private static string ApplyPathParams(string url, IList<KvPair> pathParams)
    {
        foreach (var p in pathParams)
        {
            var value = string.IsNullOrEmpty(p.Value) ? "{{" + p.Name + "}}" : p.Value;
            url = Regex.Replace(url, @":" + Regex.Escape(p.Name) + @"(?=/|$|\?|#)", value.Replace("$", "$$"));
        }
        return url;
    }

    // ---- Body ----

    private static JsonObject EmitBody(BodyConfig body)
    {
        JsonObject Text(string mime) => new() { ["mimeType"] = mime, ["text"] = Nunjucks(body.Content ?? string.Empty) };

        switch (body.Mode)
        {
            case BodyMode.Json: return Text("application/json");
            case BodyMode.Xml: return Text("application/xml");
            case BodyMode.Text:
            case BodyMode.Sparql: return Text("text/plain");
            case BodyMode.FormUrlEncoded:
                return new JsonObject { ["mimeType"] = "application/x-www-form-urlencoded", ["params"] = KvArray(body.FormData) };
            case BodyMode.MultipartForm:
            {
                var parts = new JsonArray();
                if (body.MultipartItems.Count > 0)
                {
                    foreach (var p in body.MultipartItems)
                    {
                        var isFile = string.Equals(p.Kind, "file", StringComparison.OrdinalIgnoreCase);
                        var part = new JsonObject { ["name"] = p.Name, ["disabled"] = !p.Enabled };
                        if (isFile) { part["type"] = "file"; part["fileName"] = p.Value; }
                        else part["value"] = Nunjucks(p.Value);
                        if (!string.IsNullOrEmpty(p.Description)) part["description"] = p.Description;
                        parts.Add(part);
                    }
                }
                else
                {
                    foreach (var node in KvArray(body.FormData)) parts.Add(node?.DeepClone());
                }
                return new JsonObject { ["mimeType"] = "multipart/form-data", ["params"] = parts };
            }
            case BodyMode.GraphQL:
            {
                JsonNode? variables = new JsonObject();
                if (!string.IsNullOrWhiteSpace(body.GraphQLVariables))
                {
                    try { variables = JsonNode.Parse(body.GraphQLVariables!); }
                    catch (JsonException) { variables = new JsonObject(); }
                }
                var payload = new JsonObject
                {
                    ["query"] = Nunjucks(body.GraphQLQuery ?? string.Empty),
                    ["variables"] = variables,
                };
                return new JsonObject { ["mimeType"] = "application/graphql", ["text"] = payload.ToJsonString() };
            }
            case BodyMode.Binary:
                return new JsonObject
                {
                    ["mimeType"] = string.IsNullOrEmpty(body.FileContentType) ? "application/octet-stream" : body.FileContentType,
                    ["fileName"] = body.FilePath ?? string.Empty,
                };
            default:
                return new JsonObject();
        }
    }

    // ---- Auth ----

    private static JsonObject EmitAuth(Context ctx, AuthConfig? auth, string requestName)
    {
        if (auth is null) return new JsonObject();
        var p = auth.Parameters;
        string Get(string key) => Nunjucks(p.TryGetValue(key, out var v) ? v : string.Empty);

        switch (auth.Type)
        {
            case AuthType.None:
                return new JsonObject { ["type"] = "none" };
            case AuthType.Basic:
                return new JsonObject { ["type"] = "basic", ["username"] = Get("username"), ["password"] = Get("password") };
            case AuthType.Bearer:
                return new JsonObject { ["type"] = "bearer", ["token"] = Get("token"), ["prefix"] = "" };
            case AuthType.Digest:
                return new JsonObject { ["type"] = "digest", ["username"] = Get("username"), ["password"] = Get("password") };
            case AuthType.Ntlm:
                return new JsonObject { ["type"] = "ntlm", ["username"] = Get("username"), ["password"] = Get("password") };
            case AuthType.ApiKey:
                return new JsonObject
                {
                    ["type"] = "apikey",
                    ["key"] = Get("key"),
                    ["value"] = Get("value"),
                    ["addTo"] = p.TryGetValue("placement", out var pl) && pl == "queryparams" ? "queryParams" : "header",
                };
            case AuthType.OAuth2:
                return new JsonObject
                {
                    ["type"] = "oauth2",
                    ["grantType"] = p.TryGetValue("grant_type", out var g) && !string.IsNullOrEmpty(g) ? g : "client_credentials",
                    ["accessTokenUrl"] = Get("access_token_url"),
                    ["authorizationUrl"] = Get("authorization_url"),
                    ["clientId"] = Get("client_id"),
                    ["clientSecret"] = Get("client_secret"),
                    ["scope"] = Get("scope"),
                    ["username"] = Get("username"),
                    ["password"] = Get("password"),
                    ["redirectUrl"] = Get("callback_url"),
                };
            case AuthType.AwsV4:
                return new JsonObject
                {
                    ["type"] = "iam",
                    ["accessKeyId"] = Get("accessKeyId"),
                    ["secretAccessKey"] = Get("secretAccessKey"),
                    ["sessionToken"] = Get("sessionToken"),
                    ["region"] = Get("region"),
                    ["service"] = Get("service"),
                };
            case AuthType.OAuth1:
                return new JsonObject
                {
                    ["type"] = "oauth1",
                    ["consumerKey"] = Get("consumerKey"),
                    ["consumerSecret"] = Get("consumerSecret"),
                    ["tokenKey"] = Get("token"),
                    ["tokenSecret"] = Get("tokenSecret"),
                    ["realm"] = Get("realm"),
                    ["signatureMethod"] = p.TryGetValue("signatureMethod", out var sm) && !string.IsNullOrEmpty(sm) ? sm : "HMAC-SHA1",
                };
            default:
                ctx.Warnings.Add($"{ctx.Collection.Name}: {auth.Type} auth on \"{requestName}\" has no Insomnia equivalent and was dropped.");
                return new JsonObject();
        }
    }

    // ---- helpers ----

    private static JsonArray KvArray(IEnumerable<KvPair> pairs)
    {
        var arr = new JsonArray();
        foreach (var p in pairs)
        {
            var o = new JsonObject { ["name"] = p.Name, ["value"] = Nunjucks(p.Value), ["disabled"] = !p.Enabled };
            if (!string.IsNullOrEmpty(p.Description)) o["description"] = p.Description;
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>Environment <c>data</c> object. Disabled vars are left out — Insomnia
    /// environments have no per-key enabled flag.</summary>
    private static JsonObject EnvData(IEnumerable<KvPair> vars)
    {
        var data = new JsonObject();
        foreach (var v in vars.Where(v => v.Enabled && !string.IsNullOrEmpty(v.Name)))
            data[v.Name] = v.Value;
        return data;
    }

    /// <summary>Vegha <c>{{name}}</c> → Insomnia's Nunjucks <c>{{ _.name }}</c> (the inverse of
    /// <c>InsomniaImporter.NormalizeVariables</c>). Dynamic <c>{{$…}}</c> helpers are left as-is.</summary>
    private static string Nunjucks(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return VarRef().Replace(value, m => "{{ _." + m.Groups[1].Value.Trim() + " }}");
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][\w.\-]*)\s*\}\}")]
    private static partial Regex VarRef();

    private static bool HasScripts(Collection c)
    {
        static bool Any(params string?[] s) => s.Any(x => !string.IsNullOrWhiteSpace(x));
        bool Walk(IList<RequestItem> reqs, IList<Folder> folders) =>
            reqs.Any(r => Any(r.PreRequestScript, r.PostResponseScript, r.Tests))
            || folders.Any(f => Any(f.PreRequestScript, f.PostResponseScript, f.TestsScript) || Walk(f.Requests, f.Folders));
        return Any(c.PreRequestScript, c.PostResponseScript, c.TestsScript) || Walk(c.Requests, c.Folders);
    }

    /// <summary>Deterministic resource ids (<c>req_&lt;hash&gt;</c>) so re-exporting the same
    /// collection yields a stable diff. Seeded with the collection name; a counter suffix
    /// disambiguates same-named siblings.</summary>
    private sealed class IdFactory(string seed)
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Next(string prefix, string key)
        {
            for (var i = 0; ; i++)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{prefix}|{key}|{i}"));
                var id = prefix + "_" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
                if (_used.Add(id)) return id;
            }
        }
    }
}
