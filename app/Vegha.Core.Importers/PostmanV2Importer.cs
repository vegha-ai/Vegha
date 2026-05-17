using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>Knobs for <see cref="PostmanV2Importer.ImportFromJson(string, PostmanImportOptions)"/>.</summary>
/// <param name="TranslateScripts">When true, every <c>event[].script.exec</c> block is run
/// through <see cref="PostmanScriptTranslator"/> so <c>pm.*</c> / <c>postman.*</c> calls
/// become <c>bru.*</c> / <c>req.*</c> / <c>res.*</c> calls. Defaults to <c>false</c> so the
/// no-arg overload preserves legacy "pass scripts through verbatim" behavior. UI callers
/// (the Import Wizard) flip this on by default.</param>
/// <param name="OnDiagnostic">Optional callback raised once per request whose translated
/// script still contains <c>pm.*</c> / <c>postman.*</c> tokens (patterns the regex table
/// didn't catch). The UI surfaces these as warnings in the wizard preview.</param>
public sealed record PostmanImportOptions(
    bool TranslateScripts = false,
    Action<TranslationDiagnostic>? OnDiagnostic = null);

/// <summary>
/// Imports a Postman v2.1 collection (the JSON exported from Postman) into our
/// <see cref="Collection"/> domain model. Mirrors Bruno's
/// <c>bruno-converters/src/postman/postman-to-bruno.js</c>.
///
/// Coverage:
///   - Recursive folder/item walk
///   - Method, URL (raw form), headers, query params
///   - Body modes: raw (json/text/xml/javascript/html), urlencoded, formdata, graphql
///   - Auth: basic, bearer, apikey, oauth2 (client_credentials), awsv4, digest
///   - Collection-level variable[]
///   - Event scripts: prerequest → PreRequestScript, test → Tests, with optional
///     <see cref="PostmanScriptTranslator"/> translation when <see cref="PostmanImportOptions.TranslateScripts"/>
///     is true.
///
/// Out of scope: file body, certificate config, protocolProfileBehavior tweaks,
/// "exec" arrays we currently flatten as joined-newline strings.
/// </summary>
public static class PostmanV2Importer
{
    /// <summary>Imports without translating scripts (legacy behavior — preserves <c>pm.*</c>
    /// calls verbatim, which will throw <c>ReferenceError</c> at run time).</summary>
    public static Collection ImportFromJson(string json)
        => ImportFromJson(json, new PostmanImportOptions());

    /// <summary>Imports with the supplied options. When <paramref name="options"/>'s
    /// <see cref="PostmanImportOptions.TranslateScripts"/> is true, every <c>pm.*</c> /
    /// <c>postman.*</c> script call is rewritten to its <c>bru.*</c> equivalent so the
    /// imported scripts run cleanly in Vegha's Jint sandbox.</summary>
    public static Collection ImportFromJson(string json, PostmanImportOptions options)
    {
        options ??= new PostmanImportOptions();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = root.TryGetProperty("info", out var i) ? i : default;
        var name = TryString(info, "name") ?? "Imported collection";

        var collection = new Collection { Name = name };

        // Collection-level variables (Postman: variable: [{ key, value }])
        if (root.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vars.EnumerateArray())
            {
                var k = TryString(v, "key");
                var val = TryString(v, "value") ?? string.Empty;
                if (!string.IsNullOrEmpty(k))
                {
                    collection.Variables.Add(new KvPair(k, val));
                }
            }
        }

        // Walk item[] recursively
        if (root.TryGetProperty("item", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var (requests, folders) = WalkItems(items, options);
            foreach (var r in requests) collection.Requests.Add(r);
            foreach (var f in folders) collection.Folders.Add(f);
        }

        return collection;
    }

    public static Collection ImportFromFile(string path) =>
        ImportFromJson(File.ReadAllText(path));

    public static Collection ImportFromFile(string path, PostmanImportOptions options) =>
        ImportFromJson(File.ReadAllText(path), options);

    // ============================== Item walker ==============================

    private static (List<RequestItem> Requests, List<Folder> Folders) WalkItems(JsonElement items, PostmanImportOptions options)
    {
        var requests = new List<RequestItem>();
        var folders = new List<Folder>();
        var seq = 0;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("item", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                // Folder
                var folderName = TryString(item, "name") ?? string.Empty;
                var (subRequests, subFolders) = WalkItems(nested, options);
                folders.Add(new Folder
                {
                    Name = folderName,
                    Requests = subRequests,
                    Folders = subFolders,
                });
            }
            else if (item.TryGetProperty("request", out var req))
            {
                seq++;
                var name = TryString(item, "name") ?? "Untitled";
                var built = BuildRequest(name, seq, req, item, options);
                requests.Add(built);
            }
        }
        return (requests, folders);
    }

    // ============================== Request build ==============================

    private static RequestItem BuildRequest(string name, int seq, JsonElement req, JsonElement parentItem, PostmanImportOptions options)
    {
        var method = TryString(req, "method") ?? "GET";
        var (url, queryParams, pathParams) = ExtractUrl(req);
        var headers = ExtractHeaders(req);
        var body = ExtractBody(req);
        var auth = ExtractAuth(req);
        var (preScript, tests) = ExtractEventScripts(parentItem, name, options);

        return new RequestItem
        {
            Name = name,
            Method = method.ToUpperInvariant(),
            Url = url,
            Sequence = seq,
            Params = queryParams,
            PathParams = pathParams,
            Headers = headers,
            Body = body,
            Auth = auth,
            PreRequestScript = preScript,
            Tests = tests,
        };
    }

    // ---- URL ----

    private static (string Url, List<KvPair> Query, List<KvPair> Path) ExtractUrl(JsonElement req)
    {
        if (!req.TryGetProperty("url", out var url))
            return (string.Empty, new(), new());

        // Postman url is either a string or an object with raw/host/path/query/variable.
        if (url.ValueKind == JsonValueKind.String)
            return (url.GetString() ?? string.Empty, new(), new());

        var raw = TryString(url, "raw") ?? string.Empty;

        var query = new List<KvPair>();
        if (url.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in q.EnumerateArray())
            {
                var k = TryString(p, "key");
                if (string.IsNullOrEmpty(k)) continue;
                var v = TryString(p, "value") ?? string.Empty;
                var disabled = TryBool(p, "disabled") ?? false;
                query.Add(new KvPair(k, v, !disabled));
            }
        }

        var path = new List<KvPair>();
        if (url.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in vars.EnumerateArray())
            {
                var k = TryString(p, "key");
                if (string.IsNullOrEmpty(k)) continue;
                var v = TryString(p, "value") ?? string.Empty;
                path.Add(new KvPair(k, v));
            }
        }

        return (raw, query, path);
    }

    // ---- Headers ----

    private static List<KvPair> ExtractHeaders(JsonElement req)
    {
        var list = new List<KvPair>();
        if (!req.TryGetProperty("header", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var h in headers.EnumerateArray())
        {
            var k = TryString(h, "key");
            if (string.IsNullOrEmpty(k)) continue;
            var v = TryString(h, "value") ?? string.Empty;
            var disabled = TryBool(h, "disabled") ?? false;
            list.Add(new KvPair(k, v, !disabled));
        }
        return list;
    }

    // ---- Body ----

    private static BodyConfig ExtractBody(JsonElement req)
    {
        if (!req.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return new BodyConfig { Mode = BodyMode.None };

        var mode = TryString(body, "mode") ?? "none";
        return mode switch
        {
            "raw" => ExtractRawBody(body),
            "urlencoded" => new BodyConfig
            {
                Mode = BodyMode.FormUrlEncoded,
                FormData = ExtractFormPairs(body, "urlencoded")
            },
            "formdata" => new BodyConfig
            {
                Mode = BodyMode.MultipartForm,
                FormData = ExtractFormPairs(body, "formdata")
            },
            "graphql" => ExtractGraphQLBody(body),
            "file" => new BodyConfig { Mode = BodyMode.Binary },
            _ => new BodyConfig { Mode = BodyMode.None }
        };
    }

    private static BodyConfig ExtractRawBody(JsonElement body)
    {
        var raw = TryString(body, "raw") ?? string.Empty;
        // Postman puts the language hint under options.raw.language: "json" | "xml" | "text" | "html" | "javascript"
        var lang = "text";
        if (body.TryGetProperty("options", out var opts) &&
            opts.TryGetProperty("raw", out var rawOpts) &&
            rawOpts.TryGetProperty("language", out var l) &&
            l.ValueKind == JsonValueKind.String)
        {
            lang = l.GetString() ?? "text";
        }

        var mode = lang switch
        {
            "json" => BodyMode.Json,
            "xml"  => BodyMode.Xml,
            "html" => BodyMode.Text,
            "javascript" => BodyMode.Text,
            _ => BodyMode.Text
        };
        return new BodyConfig { Mode = mode, Content = raw };
    }

    private static List<KvPair> ExtractFormPairs(JsonElement body, string field)
    {
        var list = new List<KvPair>();
        if (!body.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            var k = TryString(e, "key");
            if (string.IsNullOrEmpty(k)) continue;
            var v = TryString(e, "value") ?? string.Empty;
            var disabled = TryBool(e, "disabled") ?? false;
            list.Add(new KvPair(k, v, !disabled));
        }
        return list;
    }

    private static BodyConfig ExtractGraphQLBody(JsonElement body)
    {
        if (body.TryGetProperty("graphql", out var g))
        {
            return new BodyConfig
            {
                Mode = BodyMode.GraphQL,
                GraphQLQuery = TryString(g, "query"),
                GraphQLVariables = TryString(g, "variables"),
            };
        }
        return new BodyConfig { Mode = BodyMode.GraphQL };
    }

    // ---- Auth ----

    private static AuthConfig? ExtractAuth(JsonElement req)
    {
        if (!req.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object)
            return null;

        var type = TryString(auth, "type");
        if (string.IsNullOrEmpty(type) || type == "noauth") return null;

        // Postman stores auth params under a property named after the type, as an array of {key,value}.
        var paramsByKey = new Dictionary<string, string>();
        if (auth.TryGetProperty(type, out var paramArr) && paramArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in paramArr.EnumerateArray())
            {
                var k = TryString(p, "key");
                if (string.IsNullOrEmpty(k)) continue;
                paramsByKey[k] = TryString(p, "value") ?? string.Empty;
            }
        }

        return type switch
        {
            "basic"  => new AuthConfig
            {
                Type = AuthType.Basic,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = paramsByKey.GetValueOrDefault("username", string.Empty),
                    ["password"] = paramsByKey.GetValueOrDefault("password", string.Empty),
                }
            },
            "bearer" => new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new Dictionary<string, string>
                {
                    ["token"] = paramsByKey.GetValueOrDefault("token", string.Empty),
                }
            },
            "apikey" => new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = paramsByKey.GetValueOrDefault("key", string.Empty),
                    ["value"] = paramsByKey.GetValueOrDefault("value", string.Empty),
                    // Postman's "in" is "header" | "query" — map to our placement
                    ["placement"] = paramsByKey.GetValueOrDefault("in") == "query" ? "queryparams" : "header",
                }
            },
            "digest" => new AuthConfig
            {
                Type = AuthType.Digest,
                Parameters = new Dictionary<string, string>(paramsByKey)
            },
            "awsv4"  => new AuthConfig
            {
                Type = AuthType.AwsV4,
                Parameters = new Dictionary<string, string>
                {
                    ["accessKeyId"]     = paramsByKey.GetValueOrDefault("accessKey", string.Empty),
                    ["secretAccessKey"] = paramsByKey.GetValueOrDefault("secretKey", string.Empty),
                    ["sessionToken"]    = paramsByKey.GetValueOrDefault("sessionToken", string.Empty),
                    ["region"]          = paramsByKey.GetValueOrDefault("region", string.Empty),
                    ["service"]         = paramsByKey.GetValueOrDefault("service", string.Empty),
                }
            },
            "oauth2" => new AuthConfig
            {
                Type = AuthType.OAuth2,
                Parameters = new Dictionary<string, string>
                {
                    ["grant_type"]      = paramsByKey.GetValueOrDefault("grant_type", "client_credentials"),
                    ["access_token_url"] = paramsByKey.GetValueOrDefault("accessTokenUrl", string.Empty),
                    ["client_id"]       = paramsByKey.GetValueOrDefault("clientId", string.Empty),
                    ["client_secret"]   = paramsByKey.GetValueOrDefault("clientSecret", string.Empty),
                    ["scope"]           = paramsByKey.GetValueOrDefault("scope", string.Empty),
                    ["credentials_placement"] = paramsByKey.GetValueOrDefault("client_authentication") == "header"
                        ? "basic_auth_header" : "body",
                }
            },
            _ => new AuthConfig { Type = AuthType.None }
        };
    }

    // ---- Events (pre-request, test) ----

    private static (string? Pre, string? Tests) ExtractEventScripts(JsonElement parentItem, string requestName, PostmanImportOptions options)
    {
        if (!parentItem.TryGetProperty("event", out var events) || events.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? pre = null;
        string? tests = null;
        foreach (var e in events.EnumerateArray())
        {
            var listen = TryString(e, "listen");
            if (string.IsNullOrEmpty(listen)) continue;
            if (!e.TryGetProperty("script", out var script)) continue;
            var execLines = ExtractExec(script);
            if (string.IsNullOrEmpty(execLines)) continue;

            // When translation is enabled, rewrite pm.* / postman.* calls into bru.* / req.* / res.*
            // so the script runs in Vegha's Jint sandbox.
            if (options.TranslateScripts)
            {
                var phase = listen == "prerequest" ? "pre-request" : "tests";
                var outcome = PostmanScriptTranslator.Translate(execLines);
                execLines = outcome.TranslatedScript;
                if (outcome.UnhandledTokens.Count > 0 && options.OnDiagnostic is not null)
                {
                    options.OnDiagnostic(new TranslationDiagnostic(requestName, phase, outcome.UnhandledTokens));
                }
            }

            switch (listen)
            {
                case "prerequest": pre = execLines; break;
                case "test":       tests = execLines; break;
            }
        }
        return (pre, tests);
    }

    private static string ExtractExec(JsonElement script)
    {
        // Postman script.exec is either a string or an array of strings (lines).
        if (script.TryGetProperty("exec", out var exec))
        {
            if (exec.ValueKind == JsonValueKind.String) return exec.GetString() ?? string.Empty;
            if (exec.ValueKind == JsonValueKind.Array)
            {
                var lines = exec.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? string.Empty);
                return string.Join("\n", lines);
            }
        }
        return string.Empty;
    }

    // ============================== JsonElement helpers ==============================

    private static string? TryString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? TryBool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
