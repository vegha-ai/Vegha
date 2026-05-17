using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports an Insomnia v4 (flat <c>resources</c> + parentId) or v5 (nested
/// <c>collection</c>) export into a <see cref="Collection"/>. JSON only — YAML
/// support can be added by deserializing through YamlDotNet → JSON if a user reports
/// needing it. Mirrors <c>bruno-converters/src/insomnia/insomnia-to-bruno.js</c>.
/// </summary>
public static class InsomniaImporter
{
    public static Collection ImportFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return ImportFromString(json);
    }

    public static Collection ImportFromString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return IsV5(root) ? ParseV5(root) : ParseV4(root);
    }

    private static bool IsV5(JsonElement root)
    {
        return root.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String &&
               (type.GetString() ?? string.Empty).StartsWith("collection.insomnia.rest/5", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- v5 (nested) ----------
    private static Collection ParseV5(JsonElement root)
    {
        var name = TryGetString(root, "name") ?? "Untitled Collection";

        var requests = new List<RequestItem>();
        var folders = new List<Folder>();

        if (root.TryGetProperty("collection", out var coll) && coll.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in coll.EnumerateArray())
                AddV5Item(item, requests, folders);
        }

        return new Collection
        {
            Name = name,
            Requests = requests,
            Folders = folders,
        };
    }

    private static void AddV5Item(JsonElement item, List<RequestItem> requests, List<Folder> folders)
    {
        if (item.TryGetProperty("method", out _) && item.TryGetProperty("url", out _))
        {
            requests.Add(BuildRequestFromV5(item));
        }
        else if (item.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
        {
            var folderRequests = new List<RequestItem>();
            var nestedFolders = new List<Folder>();
            foreach (var child in kids.EnumerateArray())
                AddV5Item(child, folderRequests, nestedFolders);

            folders.Add(new Folder
            {
                Name = TryGetString(item, "name") ?? "Untitled Folder",
                Requests = folderRequests,
                Folders = nestedFolders,
            });
        }
    }

    private static RequestItem BuildRequestFromV5(JsonElement item)
    {
        var name = TryGetString(item, "name") ?? "Untitled Request";
        var method = TryGetString(item, "method") ?? "GET";
        var url = NormalizeVariables(TryGetString(item, "url") ?? string.Empty);

        var headers = ReadKvList(item, "headers");
        var parameters = ReadKvList(item, "parameters");

        var auth = ReadAuth(item);
        var body = ReadBody(item);

        return new RequestItem
        {
            Name = name,
            Method = method.ToUpperInvariant(),
            Url = url,
            Headers = headers,
            Params = parameters,
            Auth = auth,
            Body = body,
            Kind = body.Mode == BodyMode.GraphQL ? RequestKind.GraphQL : RequestKind.Http,
        };
    }

    // ---------- v4 (flat resources, parentId-linked) ----------
    private static Collection ParseV4(JsonElement root)
    {
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Insomnia export missing 'resources' array.");

        var all = resources.EnumerateArray().ToList();
        var workspace = all.FirstOrDefault(r =>
            r.TryGetProperty("_type", out var t) && t.GetString() == "workspace");

        if (workspace.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("Insomnia export does not contain a workspace resource.");

        var name = TryGetString(workspace, "name") ?? "Untitled Collection";
        var workspaceId = TryGetString(workspace, "_id") ?? string.Empty;

        var (requests, folders) = BuildV4Tree(all, workspaceId);

        return new Collection
        {
            Name = name,
            Requests = requests,
            Folders = folders,
        };
    }

    private static (List<RequestItem> Requests, List<Folder> Folders) BuildV4Tree(
        List<JsonElement> resources, string parentId)
    {
        var requests = new List<RequestItem>();
        var folders = new List<Folder>();

        foreach (var r in resources)
        {
            var type = TryGetString(r, "_type");
            var rParent = TryGetString(r, "parentId");
            if (rParent != parentId) continue;

            if (type == "request")
            {
                requests.Add(BuildRequestFromV4(r));
            }
            else if (type == "request_group")
            {
                var id = TryGetString(r, "_id") ?? string.Empty;
                var (subRequests, subFolders) = BuildV4Tree(resources, id);
                folders.Add(new Folder
                {
                    Name = TryGetString(r, "name") ?? "Untitled Folder",
                    Requests = subRequests,
                    Folders = subFolders,
                });
            }
        }

        return (requests, folders);
    }

    private static RequestItem BuildRequestFromV4(JsonElement r)
    {
        var name = TryGetString(r, "name") ?? "Untitled Request";
        var method = TryGetString(r, "method") ?? "GET";
        var url = NormalizeVariables(TryGetString(r, "url") ?? string.Empty);

        var headers = ReadKvList(r, "headers");
        var parameters = ReadKvList(r, "parameters");
        var auth = ReadAuth(r);
        var body = ReadBody(r);

        return new RequestItem
        {
            Name = name,
            Method = method.ToUpperInvariant(),
            Url = url,
            Headers = headers,
            Params = parameters,
            Auth = auth,
            Body = body,
            Kind = body.Mode == BodyMode.GraphQL ? RequestKind.GraphQL : RequestKind.Http,
        };
    }

    // ---------- shared helpers ----------

    private static List<KvPair> ReadKvList(JsonElement parent, string property)
    {
        var result = new List<KvPair>();
        if (!parent.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = TryGetString(entry, "name") ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;
            var value = NormalizeVariables(TryGetString(entry, "value") ?? string.Empty);
            var description = TryGetString(entry, "description");
            var disabled = entry.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;

            result.Add(new KvPair(name, value, !disabled) { Description = description });
        }
        return result;
    }

    private static AuthConfig? ReadAuth(JsonElement r)
    {
        if (!r.TryGetProperty("authentication", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var type = TryGetString(a, "type");
        if (string.IsNullOrEmpty(type)) return null;

        return type.ToLowerInvariant() switch
        {
            "basic" => new AuthConfig
            {
                Type = AuthType.Basic,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = NormalizeVariables(TryGetString(a, "username") ?? string.Empty),
                    ["password"] = NormalizeVariables(TryGetString(a, "password") ?? string.Empty),
                }
            },
            "bearer" => new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new Dictionary<string, string>
                {
                    ["token"] = NormalizeVariables(TryGetString(a, "token") ?? string.Empty)
                }
            },
            "digest" => new AuthConfig
            {
                Type = AuthType.Digest,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = NormalizeVariables(TryGetString(a, "username") ?? string.Empty),
                    ["password"] = NormalizeVariables(TryGetString(a, "password") ?? string.Empty),
                }
            },
            "oauth2" => new AuthConfig
            {
                Type = AuthType.OAuth2,
                Parameters = new Dictionary<string, string>
                {
                    ["grant_type"] = MapOAuth2Grant(TryGetString(a, "grantType")),
                    ["access_token_url"] = NormalizeVariables(TryGetString(a, "accessTokenUrl") ?? string.Empty),
                    ["authorization_url"] = NormalizeVariables(TryGetString(a, "authorizationUrl") ?? string.Empty),
                    ["client_id"] = NormalizeVariables(TryGetString(a, "clientId") ?? string.Empty),
                    ["client_secret"] = NormalizeVariables(TryGetString(a, "clientSecret") ?? string.Empty),
                    ["scope"] = NormalizeVariables(TryGetString(a, "scope") ?? string.Empty),
                    ["username"] = NormalizeVariables(TryGetString(a, "username") ?? string.Empty),
                    ["password"] = NormalizeVariables(TryGetString(a, "password") ?? string.Empty),
                }
            },
            "apikey" => new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = NormalizeVariables(TryGetString(a, "key") ?? "X-API-Key"),
                    ["value"] = NormalizeVariables(TryGetString(a, "value") ?? string.Empty),
                    ["placement"] = MapApiKeyPlacement(TryGetString(a, "addTo")),
                }
            },
            _ => null,
        };
    }

    private static string MapOAuth2Grant(string? insomniaGrant) => insomniaGrant switch
    {
        "authorization_code" => "authorization_code",
        "client_credentials" => "client_credentials",
        "password" => "password",
        _ => "client_credentials",
    };

    private static string MapApiKeyPlacement(string? insomnia) => insomnia switch
    {
        "queryParams" => "queryparams",
        _ => "header",
    };

    private static BodyConfig ReadBody(JsonElement r)
    {
        if (!r.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object) return new BodyConfig();
        var mimeType = (TryGetString(b, "mimeType") ?? string.Empty).Split(';')[0];

        return mimeType switch
        {
            "application/json" => new BodyConfig
            {
                Mode = BodyMode.Json,
                Content = NormalizeVariables(TryGetString(b, "text") ?? string.Empty)
            },
            "application/x-www-form-urlencoded" => new BodyConfig
            {
                Mode = BodyMode.FormUrlEncoded,
                FormData = ReadKvList(b, "params")
            },
            "multipart/form-data" => new BodyConfig
            {
                Mode = BodyMode.MultipartForm,
                FormData = ReadKvList(b, "params")
            },
            "text/plain" => new BodyConfig
            {
                Mode = BodyMode.Text,
                Content = NormalizeVariables(TryGetString(b, "text") ?? string.Empty)
            },
            "text/xml" or "application/xml" => new BodyConfig
            {
                Mode = BodyMode.Xml,
                Content = NormalizeVariables(TryGetString(b, "text") ?? string.Empty)
            },
            "application/graphql" => ParseGraphQLBody(TryGetString(b, "text") ?? string.Empty),
            _ => new BodyConfig(),
        };
    }

    private static BodyConfig ParseGraphQLBody(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var query = TryGetString(root, "query") ?? string.Empty;
            var variables = root.TryGetProperty("variables", out var v) ? v.GetRawText() : string.Empty;
            return new BodyConfig
            {
                Mode = BodyMode.GraphQL,
                GraphQLQuery = NormalizeVariables(query),
                GraphQLVariables = variables,
            };
        }
        catch
        {
            return new BodyConfig { Mode = BodyMode.GraphQL };
        }
    }

    private static string? TryGetString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(property, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Insomnia uses <c>{{ _.var }}</c> for nunjucks-templated values; Bruno uses
    /// <c>{{var}}</c>. Strip the leading <c>_.</c> and collapse whitespace inside braces.</summary>
    private static string NormalizeVariables(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(
            value, @"\{\{(.*?)\}\}",
            m =>
            {
                var inner = m.Groups[1].Value.Replace("_.", "").Replace(" ", "");
                return "{{" + inner + "}}";
            });
    }
}
