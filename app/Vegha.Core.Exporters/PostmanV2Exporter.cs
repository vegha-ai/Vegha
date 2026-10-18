using System.Text.Json;
using System.Text.Json.Nodes;
using Vegha.Core.Domain;
using Vegha.Core.Scripting.Testing;
using Vegha.Core.Importers;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.Exporters;

/// <summary>
/// Emits a Postman v2.1 collection (and <c>.postman_environment.json</c> files) from a
/// <see cref="Collection"/>. Field-for-field inverse of <see cref="PostmanV2Importer"/> /
/// <see cref="PostmanEnvironmentImporter"/>, so an export re-imports cleanly.
///
/// Lossy spots (reported as warnings):
///   - Postman has no folder/collection-level headers — they're copied into each request.
///   - gRPC and WebSocket requests have no v2.1 representation and are skipped.
///   - WSSE auth has no Postman equivalent.
///   - Request-level vars (<c>vars:pre-request</c> / <c>vars:post-response</c>) are dropped.
/// </summary>
public static class PostmanV2Exporter
{
    public const string SchemaUrl = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    public static string FileNameFor(Collection c) => ExportNaming.Sanitize(c.Name) + ".postman_collection.json";

    public static string EnvironmentFileNameFor(DomainEnv env) => ExportNaming.Sanitize(env.Name) + ".postman_environment.json";

    public static byte[] Export(Collection collection, ExportOptions options, ICollection<string> warnings)
    {
        var ctx = new Context(collection, options, warnings);
        var info = new JsonObject
        {
            ["_postman_id"] = Guid.NewGuid().ToString(),
            ["name"] = collection.Name,
            ["schema"] = SchemaUrl,
        };
        if (!string.IsNullOrEmpty(collection.Docs)) info["description"] = collection.Docs;
        var root = new JsonObject
        {
            ["info"] = info,
            ["item"] = WalkItems(ctx, collection.Requests, collection.Folders, new List<Folder>()),
        };

        var auth = EmitAuth(ctx, collection.Auth, collection.Name);
        if (auth is not null) root["auth"] = auth;
        var events = EmitEvents(ctx, collection.Name, collection.PreRequestScript, collection.PostResponseScript, collection.TestsScript,
            Scriptless.ForNode(collection.Assertions));
        if (events is not null) root["event"] = events;
        if (collection.ChainLinks.Any(l => l.Enabled)) ctx.ChainLinksDropped++;
        if (collection.Variables.Count > 0) root["variable"] = EmitVariables(collection.Variables);

        if (ctx.FolderHeadersInlined)
            warnings.Add($"{collection.Name}: collection/folder headers were copied into each request (Postman has no folder-level headers).");
        if (ctx.ChainLinksDropped > 0)
            warnings.Add($"{collection.Name}: linked requests (Run first) on {ctx.ChainLinksDropped} item(s) have no Postman equivalent and were not exported.");

        return ExportNaming.ToUtf8(root);
    }

    /// <summary>Postman environment JSON — the shape <see cref="PostmanEnvironmentImporter"/> reads.
    /// Secret flags become <c>type: "secret"</c>; values are redacted per <paramref name="includeSecrets"/>.</summary>
    public static byte[] ExportEnvironment(DomainEnv env, bool includeSecrets)
    {
        var redacted = EnvironmentExportHelper.Redact(env, includeSecrets);
        var secret = new HashSet<string>(env.SecretVariables, StringComparer.Ordinal);
        var values = new JsonArray();
        foreach (var v in redacted.Variables)
        {
            values.Add(new JsonObject
            {
                ["key"] = v.Name,
                ["value"] = v.Value,
                ["type"] = secret.Contains(v.Name) ? "secret" : "default",
                ["enabled"] = v.Enabled,
            });
        }
        var root = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = env.Name,
            ["values"] = values,
            ["_postman_variable_scope"] = "environment",
        };
        return ExportNaming.ToUtf8(root);
    }

    // ============================== Items ==============================

    private sealed class Context(Collection collection, ExportOptions options, ICollection<string> warnings)
    {
        public Collection Collection { get; } = collection;
        public ExportOptions Options { get; } = options;
        public ICollection<string> Warnings { get; } = warnings;
        public bool FolderHeadersInlined { get; set; }
        public int ChainLinksDropped { get; set; }
    }

    private static JsonArray WalkItems(Context ctx, IList<RequestItem> requests, IList<Folder> folders, List<Folder> chain)
    {
        var items = new JsonArray();
        foreach (var folder in folders)
        {
            chain.Add(folder);
            var node = new JsonObject
            {
                ["name"] = folder.Name,
                ["item"] = WalkItems(ctx, folder.Requests, folder.Folders, chain),
            };
            if (!string.IsNullOrEmpty(folder.Docs)) node["description"] = folder.Docs;
            var auth = EmitAuth(ctx, folder.Auth, folder.Name);
            if (auth is not null) node["auth"] = auth;
            var events = EmitEvents(ctx, folder.Name, folder.PreRequestScript, folder.PostResponseScript, folder.TestsScript,
                Scriptless.ForNode(folder.Assertions));
            if (events is not null) node["event"] = events;
            if (folder.ChainLinks.Any(l => l.Enabled)) ctx.ChainLinksDropped++;
            if (folder.Variables.Count > 0) node["variable"] = EmitVariables(folder.Variables);
            chain.RemoveAt(chain.Count - 1);
            items.Add(node);
        }

        foreach (var request in requests.OrderBy(r => r.Sequence))
        {
            if (!ExportTree.IsHttpLike(request))
            {
                ctx.Warnings.Add($"{ctx.Collection.Name}: skipped {request.Kind} request \"{request.Name}\" (not representable in Postman v2.1).");
                continue;
            }
            items.Add(EmitRequestItem(ctx, request, chain));
        }
        return items;
    }

    private static JsonObject EmitRequestItem(Context ctx, RequestItem r, IReadOnlyList<Folder> chain)
    {
        // Postman has no folder/collection headers: send what the request would actually send.
        var headers = r.Headers;
        if (ctx.Collection.Headers.Count > 0 || chain.Any(f => f.Headers.Count > 0))
        {
            headers = ExportTree.EffectiveHeaders(ctx.Collection, chain, r);
            ctx.FolderHeadersInlined = true;
        }
        if (r.ChainLinks.Any(l => l.Enabled)) ctx.ChainLinksDropped++;

        var request = new JsonObject
        {
            ["method"] = string.IsNullOrEmpty(r.Method) ? "GET" : r.Method.ToUpperInvariant(),
            ["header"] = EmitKvArray(headers),
            ["url"] = EmitUrl(r),
        };
        var body = EmitBody(r.Body);
        if (body is not null) request["body"] = body;
        var auth = EmitAuth(ctx, r.Auth, r.Name);
        if (auth is not null) request["auth"] = auth;
        if (!string.IsNullOrEmpty(r.Docs)) request["description"] = r.Docs;

        var item = new JsonObject { ["name"] = r.Name };
        var events = EmitEvents(ctx, r.Name, r.PreRequestScript, r.PostResponseScript, r.Tests, Scriptless.ForRequest(r));
        if (events is not null) item["event"] = events;
        item["request"] = request;
        return item;
    }

    // ---- URL ----

    private static JsonObject EmitUrl(RequestItem r)
    {
        var raw = r.Url ?? string.Empty;
        var url = new JsonObject { ["raw"] = raw };

        var noQuery = ExportTree.StripQuery(raw);
        var rest = noQuery;
        var schemeIdx = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            url["protocol"] = rest[..schemeIdx];
            rest = rest[(schemeIdx + 3)..];
        }
        var slash = rest.IndexOf('/');
        var host = slash < 0 ? rest : rest[..slash];
        if (!string.IsNullOrEmpty(host))
        {
            // Keep {{var}} hosts whole; split real hostnames on '.' the way Postman does.
            var hostParts = host.StartsWith("{{", StringComparison.Ordinal) ? new[] { host } : host.Split('.');
            url["host"] = new JsonArray(hostParts.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        }
        if (slash >= 0)
        {
            var segments = rest[(slash + 1)..].Split('/');
            url["path"] = new JsonArray(segments.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        if (r.Params.Count > 0)
        {
            var query = new JsonArray();
            foreach (var p in r.Params)
            {
                var q = new JsonObject { ["key"] = p.Name, ["value"] = p.Value };
                if (!p.Enabled) q["disabled"] = true;
                if (!string.IsNullOrEmpty(p.Description)) q["description"] = p.Description;
                query.Add(q);
            }
            url["query"] = query;
        }
        if (r.PathParams.Count > 0)
        {
            var vars = new JsonArray();
            foreach (var p in r.PathParams)
            {
                var v = new JsonObject { ["key"] = p.Name, ["value"] = p.Value };
                if (!string.IsNullOrEmpty(p.Description)) v["description"] = p.Description;
                vars.Add(v);
            }
            url["variable"] = vars;
        }
        return url;
    }

    // ---- Body ----

    private static JsonObject? EmitBody(BodyConfig body)
    {
        JsonObject Raw(string language) => new()
        {
            ["mode"] = "raw",
            ["raw"] = body.Content ?? string.Empty,
            ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = language } },
        };

        switch (body.Mode)
        {
            case BodyMode.Json: return Raw("json");
            case BodyMode.Xml: return Raw("xml");
            case BodyMode.Text:
            case BodyMode.Sparql: return Raw("text");
            case BodyMode.FormUrlEncoded:
                return new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = EmitKvArray(body.FormData) };
            case BodyMode.MultipartForm:
            {
                var parts = new JsonArray();
                if (body.MultipartItems.Count > 0)
                {
                    foreach (var p in body.MultipartItems)
                    {
                        var isFile = string.Equals(p.Kind, "file", StringComparison.OrdinalIgnoreCase);
                        var part = new JsonObject { ["key"] = p.Name, ["type"] = isFile ? "file" : "text" };
                        if (isFile) part["src"] = p.Value; else part["value"] = p.Value;
                        if (!string.IsNullOrEmpty(p.ContentType)) part["contentType"] = p.ContentType;
                        if (!p.Enabled) part["disabled"] = true;
                        if (!string.IsNullOrEmpty(p.Description)) part["description"] = p.Description;
                        parts.Add(part);
                    }
                }
                else
                {
                    foreach (var p in body.FormData)
                    {
                        var part = new JsonObject { ["key"] = p.Name, ["value"] = p.Value, ["type"] = "text" };
                        if (!p.Enabled) part["disabled"] = true;
                        parts.Add(part);
                    }
                }
                return new JsonObject { ["mode"] = "formdata", ["formdata"] = parts };
            }
            case BodyMode.GraphQL:
                return new JsonObject
                {
                    ["mode"] = "graphql",
                    ["graphql"] = new JsonObject
                    {
                        ["query"] = body.GraphQLQuery ?? string.Empty,
                        ["variables"] = body.GraphQLVariables ?? string.Empty,
                    },
                };
            case BodyMode.Binary:
                return new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = body.FilePath ?? string.Empty } };
            default:
                return null;
        }
    }

    // ---- Auth ----

    /// <summary>Null / Inherit → no <c>auth</c> key (Postman inherits from the parent);
    /// explicit None → <c>noauth</c>.</summary>
    private static JsonObject? EmitAuth(Context ctx, AuthConfig? auth, string ownerName)
    {
        if (auth is null || auth.Type == AuthType.Inherit) return null;
        var p = auth.Parameters;
        string Get(string key, string fallback = "") => p.TryGetValue(key, out var v) ? v : fallback;

        (string Type, List<(string Key, string Value)> Params)? mapped = auth.Type switch
        {
            AuthType.None => ("noauth", new()),
            AuthType.Basic => ("basic", new() { ("username", Get("username")), ("password", Get("password")) }),
            AuthType.Bearer => ("bearer", new() { ("token", Get("token")) }),
            AuthType.ApiKey => ("apikey", new()
            {
                ("key", Get("key")),
                ("value", Get("value")),
                ("in", Get("placement") == "queryparams" ? "query" : "header"),
            }),
            AuthType.Digest => ("digest", p.Select(kv => (kv.Key, kv.Value)).ToList()),
            AuthType.AwsV4 => ("awsv4", new()
            {
                ("accessKey", Get("accessKeyId")),
                ("secretKey", Get("secretAccessKey")),
                ("sessionToken", Get("sessionToken")),
                ("region", Get("region")),
                ("service", Get("service")),
            }),
            AuthType.OAuth2 => ("oauth2", OAuth2Params(p)),
            // Vegha's oauth1 keys (consumerKey, token, tokenSecret, signatureMethod, realm)
            // already match Postman's.
            AuthType.OAuth1 => ("oauth1", p.Select(kv => (kv.Key, kv.Value)).ToList()),
            AuthType.Ntlm => ("ntlm", p.Select(kv => (kv.Key, kv.Value)).ToList()),
            _ => null,
        };

        if (mapped is null)
        {
            ctx.Warnings.Add($"{ctx.Collection.Name}: {auth.Type} auth on \"{ownerName}\" has no Postman equivalent and was dropped.");
            return null;
        }

        var (type, list) = mapped.Value;
        var node = new JsonObject { ["type"] = type };
        if (type != "noauth")
        {
            var arr = new JsonArray();
            foreach (var (k, v) in list)
                arr.Add(new JsonObject { ["key"] = k, ["value"] = v, ["type"] = "string" });
            node[type] = arr;
        }
        return node;
    }

    private static List<(string, string)> OAuth2Params(IDictionary<string, string> p)
    {
        // Inverse of PostmanV2Importer's oauth2 key map; anything else passes through camelCased.
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "grant_type",
            ["access_token_url"] = "accessTokenUrl",
            ["authorization_url"] = "authUrl",
            ["client_id"] = "clientId",
            ["client_secret"] = "clientSecret",
            ["scope"] = "scope",
            ["callback_url"] = "redirect_uri",
            ["username"] = "username",
            ["password"] = "password",
        };
        var list = new List<(string, string)>();
        foreach (var (k, v) in p)
        {
            if (k == "credentials_placement")
                list.Add(("client_authentication", v == "basic_auth_header" ? "header" : "body"));
            else
                list.Add((known.TryGetValue(k, out var mapped) ? mapped : ToCamel(k), v));
        }
        return list;
    }

    private static string ToCamel(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return snake;
        return parts[0] + string.Concat(parts.Skip(1).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }

    // ---- Events ----

    /// <summary>Scriptless parts of a node, already in Postman form: request vars
    /// (prerequest), captures and assertions (test).</summary>
    private sealed record Scriptless(
        IReadOnlyList<string> PreRequestLines,
        ScriptlessToPostman.Result Captures,
        ScriptlessToPostman.Result Assertions)
    {
        private static readonly ScriptlessToPostman.Result None = new(Array.Empty<string>(), Array.Empty<string>());

        public static Scriptless ForNode(IEnumerable<KvPair> assertions) =>
            new(Array.Empty<string>(), None, ScriptlessToPostman.Assertions(assertions.Select(a => (a.Name, a.Value, a.Enabled))));

        public static Scriptless ForRequest(RequestItem r) => new(
            // Request vars may hold {{refs}} — resolve them at run time like Vegha does.
            r.PreRequestVars.Where(v => v.Enabled && !string.IsNullOrEmpty(v.Name))
                .Select(v => $"pm.variables.set({JsonSerializer.Serialize(v.Name)}, pm.variables.replaceIn({JsonSerializer.Serialize(v.Value)}));")
                .ToList(),
            ScriptlessToPostman.Captures(r.PostResponseVars.Select(p => new CaptureRule(p.Name, p.Value, CaptureScope.Runtime, p.Enabled))
                .Concat(r.PostResponseEnvVars.Select(p => new CaptureRule(p.Name, p.Value, CaptureScope.Environment, p.Enabled)))
                .Concat(r.PostResponseGlobalVars.Select(p => new CaptureRule(p.Name, p.Value, CaptureScope.Global, p.Enabled)))),
            ScriptlessToPostman.Assertions(r.Assertions.Select(a => (a.Name, a.Value, a.Enabled))));
    }

    /// <summary>Postman has two script phases: <c>prerequest</c> and <c>test</c> (which runs
    /// after the response). Vegha's steps map onto them in Vegha's execution order:
    /// prerequest = request vars → pre-request script; test = captures → post-response
    /// script → assertions → tests.</summary>
    private static JsonArray? EmitEvents(Context ctx, string ownerName, string? pre, string? post, string? tests,
        Scriptless scriptless)
    {
        var untranslated = scriptless.Captures.Untranslated.Concat(scriptless.Assertions.Untranslated).ToList();
        if (untranslated.Count > 0)
            ctx.Warnings.Add($"{ctx.Collection.Name}: \"{ownerName}\" has {untranslated.Count} capture/assertion(s) with no Postman equivalent (left as comments): {string.Join("; ", untranslated)}");

        var events = new JsonArray();
        var before = Join(string.Join("\n", scriptless.PreRequestLines), Translate(ctx, ownerName, "prerequest", pre));
        if (before.Length > 0)
            events.Add(Event("prerequest", before));

        var after = Join(
            string.Join("\n", scriptless.Captures.Lines),
            Translate(ctx, ownerName, "test", post),
            string.Join("\n", scriptless.Assertions.Lines),
            Translate(ctx, ownerName, "test", tests));
        if (after.Length > 0)
            events.Add(Event("test", after));

        return events.Count == 0 ? null : events;
    }

    private static string Join(params string?[] parts) =>
        string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Runs Vegha → Postman script translation when enabled, warning about leftovers.</summary>
    private static string? Translate(Context ctx, string ownerName, string listen, string? script)
    {
        if (string.IsNullOrWhiteSpace(script) || !ctx.Options.TranslateScripts) return script;
        var outcome = PostmanScriptTranslator.TranslateToPostman(script);
        if (outcome.UnhandledTokens.Count > 0)
            ctx.Warnings.Add($"{ctx.Collection.Name}: \"{ownerName}\" {listen} script still uses {string.Join(", ", outcome.UnhandledTokens)} — review it in Postman.");
        return outcome.TranslatedScript;
    }

    private static JsonObject Event(string listen, string script)
    {
        var exec = new JsonArray(script.Replace("\r\n", "\n").Split('\n').Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        return new JsonObject
        {
            ["listen"] = listen,
            ["script"] = new JsonObject { ["type"] = "text/javascript", ["exec"] = exec },
        };
    }

    // ---- KV helpers ----

    private static JsonArray EmitKvArray(IEnumerable<KvPair> pairs)
    {
        var arr = new JsonArray();
        foreach (var p in pairs)
        {
            var o = new JsonObject { ["key"] = p.Name, ["value"] = p.Value };
            if (!p.Enabled) o["disabled"] = true;
            if (!string.IsNullOrEmpty(p.Description)) o["description"] = p.Description;
            arr.Add(o);
        }
        return arr;
    }

    private static JsonArray EmitVariables(IEnumerable<KvPair> vars)
    {
        var arr = new JsonArray();
        foreach (var v in vars)
        {
            var o = new JsonObject { ["key"] = v.Name, ["value"] = v.Value, ["type"] = "string" };
            if (!v.Enabled) o["disabled"] = true;
            arr.Add(o);
        }
        return arr;
    }
}
