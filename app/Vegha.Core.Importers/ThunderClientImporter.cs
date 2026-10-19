using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports Thunder Client (VS Code extension) data:
/// <list type="bullet">
/// <item><description>a collection export (<c>thunder-collection_*.json</c>: <c>client</c>,
///   <c>collectionName</c>, <c>folders</c>, <c>requests</c>, <c>settings</c>);</description></item>
/// <item><description>an environment export (<c>thunder-environment_*.json</c>:
///   <c>environmentName</c>, <c>variables</c>);</description></item>
/// <item><description>the git-sync folder <c>thunder-tests/</c> — the original layout
///   (<c>thunderclient.json</c> requests + <c>thunderCollection.json</c> +
///   <c>thunderEnvironment.json</c>) and the per-file layout (<c>collections/*.json</c>,
///   <c>environments/*.json</c>).</description></item>
/// </list>
/// Scriptless tests become assertions and env captures; inline scripts use Thunder's
/// <c>tc.*</c> API, so they are carried over commented out with a note to port them.
/// <c>{{#guid}}</c>-style system variables are Thunder syntax Vegha already resolves.
/// </summary>
public static class ThunderClientImporter
{
    public const string FormatLabel = "Thunder Client collection";

    /// <summary>True for a Thunder Client collection export or a per-file git-sync collection.</summary>
    public static bool LooksLikeCollection(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("requests", out var reqs) && reqs.ValueKind == JsonValueKind.Array
        && (IsThunder(root) || root.TryGetProperty("colName", out _));

    /// <summary>True for a Thunder Client environment export.</summary>
    public static bool LooksLikeEnvironment(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && IsThunder(root) && root.TryGetProperty("environmentName", out _);

    /// <summary>True when <paramref name="dir"/> is (or directly contains) a <c>thunder-tests</c> folder.</summary>
    public static bool LooksLikeGitSyncFolder(string dir) => GitSyncRoot(dir) is not null;

    public static Collection ImportCollection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadCollection(doc.RootElement, doc.RootElement.GetProperty("requests").EnumerateArray().ToList());
    }

    public static Vegha.Core.Domain.Environment ImportEnvironment(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return ReadEnvironment(Str(root, "environmentName") ?? "Thunder environment", root, "variables");
    }

    /// <summary>Every collection in a <c>thunder-tests</c> folder. More than one collection
    /// becomes one top-level folder each under a collection named after the workspace folder;
    /// all environments are attached to the result.</summary>
    public static Collection ImportGitSyncFolder(string dir)
    {
        var root = GitSyncRoot(dir) ?? throw new InvalidDataException("No thunder-tests folder found.");
        var collections = new List<Collection>();
        var environments = new List<Vegha.Core.Domain.Environment>();

        var legacyRequests = Path.Combine(root, "thunderclient.json");
        var legacyCollections = Path.Combine(root, "thunderCollection.json");
        if (File.Exists(legacyCollections))
        {
            using var cols = JsonDocument.Parse(File.ReadAllText(legacyCollections));
            using var reqs = File.Exists(legacyRequests) ? JsonDocument.Parse(File.ReadAllText(legacyRequests)) : null;
            var allRequests = reqs?.RootElement.ValueKind == JsonValueKind.Array
                ? reqs.RootElement.EnumerateArray().ToList()
                : new List<JsonElement>();
            foreach (var col in Array(cols.RootElement).OrderBy(c => Num(c, "sortNum")))
            {
                var id = Str(col, "_id");
                collections.Add(ReadCollection(col, allRequests.Where(r => Str(r, "colId") == id).ToList()));
            }
        }
        var perFile = Path.Combine(root, "collections");
        if (Directory.Exists(perFile))
        {
            foreach (var file in Directory.EnumerateFiles(perFile, "*.json").Order(StringComparer.Ordinal))
            {
                using var col = JsonDocument.Parse(File.ReadAllText(file));
                var requests = col.RootElement.TryGetProperty("requests", out var r) ? Array(r).ToList() : new List<JsonElement>();
                collections.Add(ReadCollection(col.RootElement, requests));
            }
        }

        var legacyEnvs = Path.Combine(root, "thunderEnvironment.json");
        if (File.Exists(legacyEnvs))
        {
            using var envs = JsonDocument.Parse(File.ReadAllText(legacyEnvs));
            foreach (var env in Array(envs.RootElement).OrderBy(e => Num(e, "sortNum")))
                environments.Add(ReadEnvironment(Str(env, "name") ?? "env", env, "data"));
        }
        var perFileEnvs = Path.Combine(root, "environments");
        if (Directory.Exists(perFileEnvs))
        {
            foreach (var file in Directory.EnumerateFiles(perFileEnvs, "*.json").Order(StringComparer.Ordinal))
            {
                using var env = JsonDocument.Parse(File.ReadAllText(file));
                environments.Add(ReadEnvironment(Str(env.RootElement, "name") ?? Path.GetFileNameWithoutExtension(file), env.RootElement, "data"));
            }
        }

        if (collections.Count == 0)
            throw new InvalidDataException("The thunder-tests folder has no collections.");

        var result = collections.Count == 1
            ? collections[0]
            : new Collection
            {
                Name = Path.GetFileName(Path.GetDirectoryName(root)) is { Length: > 0 } ws ? ws : "Thunder Client",
                Folders = collections.Select(AsFolder).ToList(),
            };
        return result with { Environments = environments };
    }

    // ------------------------------------------------------------------ collections

    private static Collection ReadCollection(JsonElement col, IReadOnlyList<JsonElement> requests)
    {
        var name = Str(col, "collectionName") ?? Str(col, "colName") ?? "Thunder Client";
        var settings = col.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var folders = col.TryGetProperty("folders", out var f) ? Array(f).ToList() : new List<JsonElement>();

        // Folders nest through containerId; requests sit in a folder (containerId) or the root.
        var byParent = folders.GroupBy(x => Str(x, "containerId") ?? string.Empty).ToDictionary(g => g.Key, g => g.ToList());
        var requestsByParent = requests.GroupBy(x => Str(x, "containerId") ?? string.Empty).ToDictionary(g => g.Key, g => g.ToList());

        Folder BuildFolder(JsonElement folder, int depth)
        {
            var id = Str(folder, "_id") ?? string.Empty;
            var fs = folder.TryGetProperty("settings", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
            var (tests, envCaptures) = Settings(fs);
            return new Folder
            {
                Name = Str(folder, "name") ?? "Folder",
                Folders = depth > 32 ? new List<Folder>() : Children(byParent, id).Select(c => BuildFolder(c, depth + 1)).ToList(),
                Requests = Requests(requestsByParent, id),
                Headers = Kv(fs, "headers"),
                Auth = Auth(fs, defaultInherit: true),
                PreRequestScript = Script(fs, "preReq"),
                PostResponseScript = Script(fs, "postReq"),
                Assertions = tests,
                Docs = Str(fs, "docs"),
            };
        }

        var (rootTests, _) = Settings(settings);
        return new Collection
        {
            Name = name,
            Folders = Children(byParent, string.Empty).Select(c => BuildFolder(c, 0)).ToList(),
            Requests = Requests(requestsByParent, string.Empty),
            Headers = Kv(settings, "headers"),
            Auth = Auth(settings, defaultInherit: false),
            PreRequestScript = Script(settings, "preReq"),
            PostResponseScript = Script(settings, "postReq"),
            Assertions = rootTests,
            Docs = Str(settings, "docs"),
            Presets = Str(settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("options", out var o) ? o : default, "baseUrl") is { Length: > 0 } baseUrl
                ? new RequestPresets { BaseUrl = baseUrl }
                : null,
        };
    }

    private static List<JsonElement> Children(Dictionary<string, List<JsonElement>> byParent, string id) =>
        byParent.TryGetValue(id, out var list) ? list.OrderBy(x => Num(x, "sortNum")).ToList() : new List<JsonElement>();

    private static List<RequestItem> Requests(Dictionary<string, List<JsonElement>> byParent, string id) =>
        Children(byParent, id).Select((r, i) => ReadRequest(r, i + 1)).ToList();

    private static (IList<KvPair> Assertions, IList<KvPair> EnvCaptures) Settings(JsonElement settings)
    {
        var assertions = new List<KvPair>();
        var captures = new List<KvPair>();
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("tests", out var tests))
            foreach (var t in Array(tests)) ReadTest(t, assertions, captures, captures);
        return (assertions, captures);
    }

    private static Folder AsFolder(Collection c) => new()
    {
        Name = c.Name,
        Folders = c.Folders,
        Requests = c.Requests,
        Headers = c.Headers,
        Auth = c.Auth is { Type: not AuthType.None } ? c.Auth : null,
        PreRequestScript = c.PreRequestScript,
        PostResponseScript = c.PostResponseScript,
        Assertions = c.Assertions,
        Docs = c.Docs,
    };

    // ------------------------------------------------------------------ requests

    private static RequestItem ReadRequest(JsonElement r, int sequence)
    {
        var url = Str(r, "url") ?? string.Empty;
        var allParams = r.TryGetProperty("params", out var p) ? Array(p).ToList() : new List<JsonElement>();
        var pathParams = allParams.Where(x => Bool(x, "isPath")).Select(ToKv).ToList();
        foreach (var pp in pathParams)
            url = url.Replace("{" + pp.Name + "}", ":" + pp.Name, StringComparison.Ordinal); // Thunder {id} → :id

        var assertions = new List<KvPair>();
        var envCaptures = new List<KvPair>();
        var globalCaptures = new List<KvPair>();
        if (r.TryGetProperty("tests", out var tests))
            foreach (var t in Array(tests)) ReadTest(t, assertions, envCaptures, globalCaptures);

        var body = Body(r, out var isGraphQL);
        return new RequestItem
        {
            Name = Str(r, "name") ?? "Request",
            Kind = isGraphQL ? RequestKind.GraphQL : RequestKind.Http,
            Method = (Str(r, "method") ?? "GET").ToUpperInvariant(),
            Url = url,
            Sequence = sequence,
            Params = allParams.Where(x => !Bool(x, "isPath")).Select(ToKv).ToList(),
            PathParams = pathParams,
            Headers = Kv(r, "headers"),
            Body = body,
            Auth = Auth(r, defaultInherit: true),
            Assertions = assertions,
            PostResponseEnvVars = envCaptures,
            PostResponseGlobalVars = globalCaptures,
            PreRequestScript = Script(r, "preReq"),
            PostResponseScript = Script(r, "postReq"),
            Docs = Str(r, "docs"),
        };
    }

    private static BodyConfig Body(JsonElement r, out bool isGraphQL)
    {
        isGraphQL = false;
        if (!r.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object) return new BodyConfig();
        switch ((Str(b, "type") ?? "none").ToLowerInvariant())
        {
            case "json": return new BodyConfig { Mode = BodyMode.Json, Content = Str(b, "raw") ?? string.Empty };
            case "xml": return new BodyConfig { Mode = BodyMode.Xml, Content = Str(b, "raw") ?? string.Empty };
            case "text": return new BodyConfig { Mode = BodyMode.Text, Content = Str(b, "raw") ?? string.Empty };
            case "formencoded":
                return new BodyConfig { Mode = BodyMode.FormUrlEncoded, FormData = Kv(b, "form") };
            case "formdata":
            {
                var text = Kv(b, "form");
                var files = (b.TryGetProperty("files", out var fl) ? Array(fl) : Enumerable.Empty<JsonElement>())
                    .Select(x => new KvPair(Str(x, "name") ?? string.Empty, "@file(" + (Str(x, "value") ?? string.Empty) + ")", !Bool(x, "isDisabled")))
                    .ToList();
                return new BodyConfig
                {
                    Mode = BodyMode.MultipartForm,
                    FormData = text.Concat(files).ToList(),
                    MultipartItems = text.Select(t => new MultipartFormItem { Name = t.Name, Value = t.Value, Enabled = t.Enabled })
                        .Concat(files.Select(x => new MultipartFormItem { Name = x.Name, Value = x.Value[6..^1], Kind = "file", Enabled = x.Enabled }))
                        .ToList(),
                };
            }
            case "graphql":
                isGraphQL = true;
                var g = b.TryGetProperty("graphql", out var gq) ? gq : default;
                return new BodyConfig { Mode = BodyMode.GraphQL, GraphQLQuery = Str(g, "query") ?? string.Empty, GraphQLVariables = Str(g, "variables") };
            case "binary":
                var path = Str(b, "binary") ?? string.Empty;
                return new BodyConfig { Mode = BodyMode.Binary, FilePath = path, FormData = new List<KvPair> { new("file", "@file(" + path + ")") } };
            default:
                return new BodyConfig();
        }
    }

    // ------------------------------------------------------------------ auth

    private static AuthConfig? Auth(JsonElement owner, bool defaultInherit)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("auth", out var a) || a.ValueKind != JsonValueKind.Object)
            return defaultInherit ? new AuthConfig { Type = AuthType.Inherit } : null;
        var type = (Str(a, "type") ?? "none").ToLowerInvariant();
        JsonElement Sub(string name) => a.TryGetProperty(name, out var x) ? x : default;
        AuthConfig Make(AuthType t, params (string Key, string? Value)[] ps) => new()
        {
            Type = t,
            Parameters = ps.ToDictionary(x => x.Key, x => x.Value ?? string.Empty),
        };

        return type switch
        {
            "inherit" => new AuthConfig { Type = AuthType.Inherit },
            "none" => new AuthConfig { Type = AuthType.None },
            "basic" => Make(AuthType.Basic, ("username", Str(Sub("basic"), "username")), ("password", Str(Sub("basic"), "password"))),
            "bearer" => Make(AuthType.Bearer, ("token", Str(a, "bearer") ?? Str(Sub("bearer"), "token"))),
            "digest" => Make(AuthType.Digest, ("username", Str(Sub("digest"), "username") ?? Str(Sub("basic"), "username")),
                ("password", Str(Sub("digest"), "password") ?? Str(Sub("basic"), "password"))),
            "ntlm" => Make(AuthType.Ntlm, ("username", Str(Sub("ntlm"), "username")), ("password", Str(Sub("ntlm"), "password")),
                ("domain", Str(Sub("ntlm"), "domain"))),
            "aws" => Make(AuthType.AwsV4, ("accessKeyId", Str(Sub("aws"), "accessKeyId")), ("secretAccessKey", Str(Sub("aws"), "secretKey") ?? Str(Sub("aws"), "secretAccessKey")),
                ("sessionToken", Str(Sub("aws"), "sessionToken")), ("region", Str(Sub("aws"), "region")), ("service", Str(Sub("aws"), "service"))),
            "oauth1" => Make(AuthType.OAuth1, ("consumerKey", Str(Sub("oauth1"), "consumerKey")), ("consumerSecret", Str(Sub("oauth1"), "consumerSecret")),
                ("token", Str(Sub("oauth1"), "token")), ("tokenSecret", Str(Sub("oauth1"), "tokenSecret")),
                ("signatureMethod", Str(Sub("oauth1"), "signatureMethod") ?? "HMAC-SHA1"), ("realm", Str(Sub("oauth1"), "realm"))),
            "oauth2" => OAuth2(Sub("oauth2")),
            "apikey" => Make(AuthType.ApiKey, ("key", Str(Sub("apikey"), "key") ?? "X-API-Key"), ("value", Str(Sub("apikey"), "value")),
                ("placement", string.Equals(Str(Sub("apikey"), "addTo"), "query", StringComparison.OrdinalIgnoreCase) ? "queryparams" : "header")),
            _ => null,
        };
    }

    private static AuthConfig OAuth2(JsonElement o)
    {
        var grant = (Str(o, "grantType") ?? "client_credentials") switch
        {
            "authorization_code" or "authorization_code_pkce" => "authorization_code",
            "password" => "password",
            "implicit" => "implicit",
            _ => "client_credentials",
        };
        var ps = new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["access_token_url"] = Str(o, "tokenUrl") ?? string.Empty,
            ["authorization_url"] = Str(o, "authUrl") ?? string.Empty,
            ["callback_url"] = Str(o, "callbackUrl") ?? string.Empty,
            ["client_id"] = Str(o, "clientId") ?? string.Empty,
            ["client_secret"] = Str(o, "clientSecret") ?? string.Empty,
            ["scope"] = Str(o, "scope") ?? string.Empty,
            ["username"] = Str(o, "username") ?? string.Empty,
            ["password"] = Str(o, "password") ?? string.Empty,
        };
        if (string.Equals(Str(o, "grantType"), "authorization_code_pkce", StringComparison.Ordinal)) ps["use_pkce"] = "true";
        return new AuthConfig { Type = AuthType.OAuth2, Parameters = ps };
    }

    // ------------------------------------------------------------------ tests → assertions

    /// <summary>One Thunder scriptless test → a Vegha assertion (<c>res.status: eq 200</c>) or
    /// an environment capture (<c>set-env-var</c>). Unknown kinds are dropped.</summary>
    private static void ReadTest(JsonElement t, List<KvPair> assertions, List<KvPair> envCaptures, List<KvPair> globalCaptures)
    {
        var type = Str(t, "type") ?? string.Empty;
        var custom = Str(t, "custom") ?? string.Empty;
        var action = Str(t, "action") ?? "equal";
        var value = Str(t, "value") ?? string.Empty;

        if (type == "set-env-var")
        {
            // value is "{{name}}", "{{name, local}}" or "{{name, global}}"
            var m = Regex.Match(value, @"^\s*\{\{\s*([^,}\s]+)\s*(?:,\s*(\w+)\s*)?\}\}\s*$");
            if (!m.Success || Lhs(custom) is not { } source) return;
            var target = string.Equals(m.Groups[2].Value, "global", StringComparison.OrdinalIgnoreCase) ? globalCaptures : envCaptures;
            target.Add(new KvPair(m.Groups[1].Value, source));
            return;
        }

        var lhs = type switch
        {
            "res-code" => "res.status",
            "res-time" => "res.responseTime",
            "res-body" => "res.body",
            "Content-Type" or "Content-Length" or "Content-Encoding" => $"res.headers['{type.ToLowerInvariant()}']",
            "res-header" or "custom-header" => $"res.headers['{custom.ToLowerInvariant()}']",
            "json-query" => Lhs(custom),
            _ => null,
        };
        if (lhs is null || Operator(action, value) is not { } op) return;
        assertions.Add(new KvPair(lhs, op, enabled: !Bool(t, "isDisabled")));
    }

    /// <summary>Thunder's response selectors (<c>json.a.b</c>, <c>header.X</c>, <c>text</c>) as
    /// Vegha expressions.</summary>
    private static string? Lhs(string custom)
    {
        var c = custom.Trim();
        if (c is "json" or "") return "res.body";
        if (c == "text") return "res.body";
        if (c.StartsWith("json.", StringComparison.Ordinal) || c.StartsWith("json[", StringComparison.Ordinal))
            return "res.body" + c["json".Length..];
        if (c.StartsWith("header.", StringComparison.OrdinalIgnoreCase))
            return $"res.headers['{c["header.".Length..].ToLowerInvariant()}']";
        if (c.StartsWith("cookie.", StringComparison.OrdinalIgnoreCase)) return null; // no cookie selector
        return "res.body." + c;
    }

    private static string? Operator(string action, string value)
    {
        var v = Literal(value);
        return action switch
        {
            "equal" or "=" => "eq " + v,
            "notequal" or "!=" => "neq " + v,
            "contains" => "contains " + v,
            "notcontains" => "notContains " + v,
            "<" or "lessthan" => "lt " + v,
            "<=" or "lessthanequal" => "lte " + v,
            ">" or "greaterthan" => "gt " + v,
            ">=" or "greaterthanequal" => "gte " + v,
            "count" => "length " + v,
            "isjson" => "isJson",
            "regex" => "matches " + value,
            "type" or "istype" => value.ToLowerInvariant() switch
            {
                "string" => "isString",
                "number" => "isNumber",
                "boolean" => "isBoolean",
                "array" => "isArray",
                "object" => "isObject",
                "null" => "isNull",
                "undefined" => "isUndefined",
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>Numbers, booleans, null and {{vars}} stay bare; other text is quoted.</summary>
    private static string Literal(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return "\"\"";
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return v;
        if (v is "true" or "false" or "null") return v;
        if (v.StartsWith("{{", StringComparison.Ordinal) && v.EndsWith("}}", StringComparison.Ordinal)) return v;
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v;
        return "\"" + v.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    // ------------------------------------------------------------------ scripts

    /// <summary>Thunder's inline scripts use the <c>tc.*</c> API, which Vegha doesn't run —
    /// keep them, commented out, so nothing breaks at send time and nothing is lost.</summary>
    private static string? Script(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var block) || block.ValueKind != JsonValueKind.Object)
            return null;
        if (!block.TryGetProperty("inlineScripts", out var scripts)) return null;
        var lines = new List<string>();
        foreach (var s in Array(scripts))
        {
            if (!s.TryGetProperty("script", out var body)) continue;
            if (body.ValueKind == JsonValueKind.Array) lines.AddRange(body.EnumerateArray().Select(l => l.GetString() ?? string.Empty));
            else if (body.ValueKind == JsonValueKind.String) lines.AddRange((body.GetString() ?? string.Empty).Split('\n'));
        }
        if (lines.All(string.IsNullOrWhiteSpace)) return null;
        var sb = new StringBuilder();
        sb.AppendLine("// Imported from Thunder Client. It used the tc.* API, which Vegha doesn't run —");
        sb.AppendLine("// port it to bru.* (e.g. tc.setVar → bru.setVar, tc.response → res) and uncomment.");
        foreach (var line in lines) sb.Append("// ").AppendLine(line.TrimEnd('\r'));
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ environments

    private static Vegha.Core.Domain.Environment ReadEnvironment(string name, JsonElement env, string variablesProperty)
    {
        var vars = new List<KvPair>();
        var secrets = new List<string>();
        var source = env.TryGetProperty(variablesProperty, out var v) ? v : env.TryGetProperty("variables", out var alt) ? alt : default;
        foreach (var x in Array(source))
        {
            var n = Str(x, "name");
            if (string.IsNullOrEmpty(n)) continue;
            vars.Add(new KvPair(n, Str(x, "value") ?? string.Empty, !Bool(x, "isDisabled")));
            if (Bool(x, "isSecret")) secrets.Add(n);
        }
        return new Vegha.Core.Domain.Environment
        {
            Id = Str(env, "_id") ?? Guid.NewGuid().ToString(),
            Name = name,
            Variables = vars,
            SecretVariables = secrets,
        };
    }

    // ------------------------------------------------------------------ JSON helpers

    private static bool IsThunder(JsonElement root) =>
        string.Equals(Str(root, "client"), "Thunder Client", StringComparison.OrdinalIgnoreCase);

    private static string? GitSyncRoot(string dir)
    {
        foreach (var candidate in new[] { dir, Path.Combine(dir, "thunder-tests") })
        {
            if (!Directory.Exists(candidate)) continue;
            if (File.Exists(Path.Combine(candidate, "thunderCollection.json"))
                || (Directory.Exists(Path.Combine(candidate, "collections"))
                    && string.Equals(Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "thunder-tests", StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                _ => null,
            }
            : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static KvPair ToKv(JsonElement x) =>
        new(Str(x, "name") ?? string.Empty, Str(x, "value") ?? string.Empty, !Bool(x, "isDisabled"));

    private static IList<KvPair> Kv(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var list)
            ? Array(list).Select(ToKv).Where(k => k.Name.Length > 0).ToList()
            : new List<KvPair>();
}
