using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports an OpenAPI 3.x or Swagger 2.0 spec into a <see cref="Collection"/>. Operations
/// become requests (one per method+path), tags become folders, security schemes map to
/// bearer/basic/apikey/oauth2 auth configs, server variables expand into the baseUrl and
/// individual <see cref="KvPair"/> variables, and request bodies + parameters are
/// pre-populated with examples or sample values (including allOf/oneOf/anyOf composition).
/// </summary>
public static class OpenApiImporter
{
    public static Collection ImportFromFile(string path) =>
        ImportFromString(File.ReadAllText(path));

    public static Collection ImportFromString(string spec)
    {
        OpenApiDocument? doc;
        try { doc = ReadDocument(spec); }
        catch (InvalidCastException)
        {
            // Real-world specs occasionally leave components with null/empty values (a
            // dangling `Schema:` line in YAML or `"Schema": null` in JSON). The reader
            // crashes with InvalidCastException on those. Strip the bad entries and retry.
            var sanitized = SanitizeNullComponents(spec);
            doc = ReadDocument(sanitized);
        }
        return ToCollection(doc);
    }

    private static OpenApiDocument ReadDocument(string spec)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(spec));
        var reader = new OpenApiStreamReader();
        try
        {
            var doc = reader.Read(stream, out var diagnostic);
            if (doc is null)
                throw new InvalidDataException(
                    "OpenAPI: spec parse failed. " +
                    string.Join("; ", diagnostic.Errors.Select(e => e.Message)));
            return doc;
        }
        catch (Microsoft.OpenApi.Exceptions.OpenApiException ex)
        {
            throw new InvalidDataException("OpenAPI: " + ex.Message, ex);
        }
        catch (Microsoft.OpenApi.Readers.Exceptions.OpenApiUnsupportedSpecVersionException ex)
        {
            throw new InvalidDataException("OpenAPI: " + ex.Message, ex);
        }
    }

    // ---------------- spec sanitation (defects in real-world inputs) ----------------

    /// <summary>Removes entries under <c>components.*</c> whose value is null/empty (a
    /// common defect in hand-written specs where someone left a placeholder line dangling).
    /// The reader otherwise crashes with a YamlScalarNode→YamlMappingNode cast error.</summary>
    private static string SanitizeNullComponents(string spec)
    {
        // JSON path — clean, structural.
        var trimmed = spec.TrimStart();
        if (trimmed.StartsWith("{"))
        {
            try
            {
                var root = JsonNode.Parse(spec);
                if (root?["components"] is JsonObject components)
                {
                    foreach (var (_, section) in components.ToList())
                    {
                        if (section is not JsonObject map) continue;
                        var dead = map.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList();
                        foreach (var k in dead) map.Remove(k);
                    }
                    return root.ToJsonString();
                }
            }
            catch { /* fall through to YAML pass */ }
        }

        // YAML path — regex-strip dangling component entries. We look for an indented
        // `<key>:` with no inline value, followed immediately by a sibling/parent line
        // (no nested content). A next line starting with `-` is treated as a list-style
        // child even when at the same indent (legal in YAML).
        return s_yamlDanglingEntry.Replace(spec, m =>
        {
            var keyIndent = m.Groups["indent"].Length;
            var nextIndent = m.Groups["nextIndent"].Length;
            var nextLine = m.Groups["next"].Value;
            var firstNonSpace = nextLine.Length > nextIndent ? nextLine[nextIndent] : ' ';
            // YAML same-indent list items still belong to the parent — keep.
            if (firstNonSpace == '-') return m.Value;
            return nextIndent <= keyIndent ? nextLine : m.Value;
        });
    }

    private static readonly Regex s_yamlDanglingEntry = new(
        @"^(?<indent>[ \t]+)(?<key>[A-Za-z0-9_-]+):[ \t]*\r?\n(?<next>(?<nextIndent>[ \t]*)\S[^\r\n]*\r?\n)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Materializes the OpenAPI document. Public so drift detection can re-parse
    /// and diff against the same projection format.</summary>
    public static Collection ToCollection(OpenApiDocument doc)
    {
        var name = doc.Info?.Title ?? "OpenAPI Collection";
        var version = doc.Info?.Version ?? "1.0";

        // Server URL + per-variable KvPairs. We pick the first server (most specs list
        // multiple environments and order them dev→stage→prod, so the first is the
        // "default" starting point users can override via the variables).
        var (baseUrl, serverVars) = ResolveServer(doc.Servers?.FirstOrDefault());

        // Collection-level auth from the spec's top-level security requirement.
        var collectionAuthSchemes = ResolveSchemes(doc.SecurityRequirements);
        var (collectionAuth, authVars) = BuildAuth(collectionAuthSchemes);

        var variables = new List<KvPair>();
        if (!string.IsNullOrEmpty(baseUrl))
            variables.Add(new KvPair("baseUrl", baseUrl));
        variables.AddRange(serverVars);
        variables.AddRange(authVars);

        var rootRequests = new List<RequestItem>();
        var foldersByTag = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);

        if (doc.Paths is not null)
        {
            foreach (var (path, pathItem) in doc.Paths)
            {
                if (pathItem.Operations is null) continue;
                foreach (var (op, operation) in pathItem.Operations)
                {
                    var request = BuildRequest(
                        path, op.ToString().ToUpperInvariant(), operation, pathItem, baseUrl,
                        collectionAuthSchemes);
                    var tag = operation.Tags?.FirstOrDefault()?.Name;
                    if (string.IsNullOrEmpty(tag))
                    {
                        rootRequests.Add(request);
                    }
                    else
                    {
                        if (!foldersByTag.TryGetValue(tag, out var folder))
                        {
                            folder = new Folder { Name = tag, Requests = new List<RequestItem>() };
                            foldersByTag[tag] = folder;
                        }
                        ((List<RequestItem>)folder.Requests).Add(request);
                    }
                }
            }
        }

        return new Collection
        {
            Name = name,
            Version = version,
            Variables = variables,
            Requests = rootRequests,
            Folders = foldersByTag.Values.OrderBy(f => f.Name).ToList(),
            Auth = collectionAuth,
        };
    }

    private static RequestItem BuildRequest(
        string path, string method, OpenApiOperation operation,
        OpenApiPathItem pathItem, string baseUrl,
        IList<OpenApiSecurityScheme> collectionAuthSchemes)
    {
        var name = !string.IsNullOrEmpty(operation.OperationId)
            ? operation.OperationId
            : $"{method} {path}";

        // URL: baseUrl reference + the path (with {placeholders} kept verbatim — Bruno
        // resolves them via path params).
        var url = string.IsNullOrEmpty(baseUrl)
            ? path
            : "{{baseUrl}}" + path;

        var queryParams = new List<KvPair>();
        var pathParams = new List<KvPair>();
        var headers = new List<KvPair>();

        // Merge path-level + operation-level parameters; operation-level wins on name conflict.
        var allParams = new List<OpenApiParameter>();
        if (pathItem.Parameters is not null) allParams.AddRange(pathItem.Parameters);
        if (operation.Parameters is not null) allParams.AddRange(operation.Parameters);

        foreach (var param in allParams)
        {
            var sample = SampleFromSchema(param.Schema, new HashSet<OpenApiSchema>());
            switch (param.In)
            {
                case ParameterLocation.Query:
                    queryParams.Add(new KvPair(param.Name, sample, true) { Description = param.Description });
                    break;
                case ParameterLocation.Path:
                    pathParams.Add(new KvPair(param.Name, sample, true) { Description = param.Description });
                    break;
                case ParameterLocation.Header:
                    headers.Add(new KvPair(param.Name, sample, true) { Description = param.Description });
                    break;
            }
        }

        // Body: pull the first JSON/form/text request body if present.
        var body = new BodyConfig();
        var docsNote = string.Empty;
        if (operation.RequestBody?.Content is not null)
        {
            if (operation.RequestBody.Content.TryGetValue("application/json", out var json))
            {
                body = new BodyConfig
                {
                    Mode = BodyMode.Json,
                    Content = SampleFromSchema(json.Schema, new HashSet<OpenApiSchema>(), asObject: true),
                };
                headers.Add(new KvPair("Content-Type", "application/json"));
                docsNote = DescribeComposition(json.Schema);
            }
            else if (operation.RequestBody.Content.TryGetValue("application/x-www-form-urlencoded", out var form))
            {
                body = new BodyConfig
                {
                    Mode = BodyMode.FormUrlEncoded,
                    FormData = SampleFormFromSchema(form.Schema),
                };
            }
            else if (operation.RequestBody.Content.TryGetValue("text/plain", out var text))
            {
                body = new BodyConfig
                {
                    Mode = BodyMode.Text,
                    Content = text.Example?.ToString() ?? string.Empty,
                };
            }
        }

        // Operation-level security override: only emit when it materially differs from
        // the collection-level requirement, otherwise inherit.
        AuthConfig? opAuth = null;
        if (operation.Security is not null && operation.Security.Count > 0)
        {
            var opSchemes = ResolveSchemes(operation.Security);
            if (!SameSchemeSet(opSchemes, collectionAuthSchemes))
            {
                (opAuth, _) = BuildAuth(opSchemes);
            }
        }

        var docs = operation.Description ?? operation.Summary;
        if (!string.IsNullOrEmpty(docsNote))
            docs = string.IsNullOrEmpty(docs) ? docsNote : docs + "\n\n" + docsNote;

        return new RequestItem
        {
            Name = name,
            Method = method,
            Url = url,
            Params = queryParams,
            PathParams = pathParams,
            Headers = headers,
            Body = body,
            Auth = opAuth,
            Docs = docs,
        };
    }

    // ---------------- server variables ----------------

    /// <summary>Expands `{var}` placeholders in the server URL using each variable's
    /// declared default, and returns the per-variable KvPairs so users can override them
    /// in the workspace UI. Enum choices land in the variable's <c>Description</c>.</summary>
    private static (string Url, List<KvPair> Vars) ResolveServer(OpenApiServer? server)
    {
        if (server is null) return (string.Empty, new List<KvPair>());

        var url = server.Url ?? string.Empty;
        var vars = new List<KvPair>();
        if (server.Variables is null || server.Variables.Count == 0)
            return (url, vars);

        foreach (var (name, variable) in server.Variables)
        {
            var def = variable.Default ?? string.Empty;
            url = url.Replace("{" + name + "}", def, StringComparison.Ordinal);

            var desc = variable.Description;
            if (variable.Enum is not null && variable.Enum.Count > 0)
            {
                var enums = string.Join(", ", variable.Enum);
                desc = string.IsNullOrEmpty(desc)
                    ? $"one of: {enums}"
                    : $"{desc} (one of: {enums})";
            }
            vars.Add(new KvPair(name, def, true) { Description = desc });
        }
        return (url, vars);
    }

    // ---------------- security / auth ----------------

    /// <summary>Pulls the schemes referenced by the *first* security requirement. We
    /// don't try to merge alternatives (OR-of-AND in OpenAPI) — the user can switch via
    /// the auth panel.</summary>
    private static IList<OpenApiSecurityScheme> ResolveSchemes(IList<OpenApiSecurityRequirement>? reqs)
    {
        var result = new List<OpenApiSecurityScheme>();
        if (reqs is null || reqs.Count == 0) return result;
        var first = reqs[0];
        foreach (var (scheme, _) in first)
            if (scheme is not null) result.Add(scheme);
        return result;
    }

    private static bool SameSchemeSet(IList<OpenApiSecurityScheme> a, IList<OpenApiSecurityScheme> b)
    {
        if (a.Count != b.Count) return false;
        var aIds = a.Select(s => s.Reference?.Id ?? s.Name ?? string.Empty).OrderBy(x => x).ToArray();
        var bIds = b.Select(s => s.Reference?.Id ?? s.Name ?? string.Empty).OrderBy(x => x).ToArray();
        for (var i = 0; i < aIds.Length; i++)
            if (!string.Equals(aIds[i], bIds[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Maps the first scheme to an <see cref="AuthConfig"/>. Returns the
    /// `{{templated}}` Collection.Variables placeholders the user is expected to fill
    /// in (token, username, password, client_id, client_secret).</summary>
    private static (AuthConfig? Auth, List<KvPair> Vars) BuildAuth(IList<OpenApiSecurityScheme> schemes)
    {
        if (schemes.Count == 0) return (null, new List<KvPair>());
        var scheme = schemes[0];
        switch (scheme.Type)
        {
            case SecuritySchemeType.Http:
                if (string.Equals(scheme.Scheme, "bearer", StringComparison.OrdinalIgnoreCase))
                {
                    return (new AuthConfig
                    {
                        Type = AuthType.Bearer,
                        Parameters = new Dictionary<string, string> { ["token"] = "{{token}}" },
                    }, new List<KvPair> { new("token", string.Empty, true) { Description = "Bearer token" } });
                }
                if (string.Equals(scheme.Scheme, "basic", StringComparison.OrdinalIgnoreCase))
                {
                    return (new AuthConfig
                    {
                        Type = AuthType.Basic,
                        Parameters = new Dictionary<string, string>
                        {
                            ["username"] = "{{username}}",
                            ["password"] = "{{password}}",
                        },
                    }, new List<KvPair>
                    {
                        new("username", string.Empty, true) { Description = "Basic auth username" },
                        new("password", string.Empty, true) { Description = "Basic auth password" },
                    });
                }
                return (null, new List<KvPair>());

            case SecuritySchemeType.ApiKey:
                var placement = scheme.In switch
                {
                    ParameterLocation.Query => "query",
                    ParameterLocation.Cookie => "cookie",
                    _ => "header",
                };
                return (new AuthConfig
                {
                    Type = AuthType.ApiKey,
                    Parameters = new Dictionary<string, string>
                    {
                        ["key"] = scheme.Name ?? "X-API-Key",
                        ["value"] = "{{apiKey}}",
                        ["placement"] = placement,
                    },
                }, new List<KvPair> { new("apiKey", string.Empty, true) { Description = "API key value" } });

            case SecuritySchemeType.OAuth2:
                return BuildOAuth2(scheme);

            default:
                return (null, new List<KvPair>());
        }
    }

    private static (AuthConfig? Auth, List<KvPair> Vars) BuildOAuth2(OpenApiSecurityScheme scheme)
    {
        // Pick the first flow that's declared. Order: clientCredentials → password →
        // authorizationCode → implicit. The Bruno auth panel uses snake_case grant types.
        if (scheme.Flows is null) return (null, new List<KvPair>());
        OpenApiOAuthFlow? flow;
        string grant;
        if ((flow = scheme.Flows.ClientCredentials) is not null) grant = "client_credentials";
        else if ((flow = scheme.Flows.Password) is not null) grant = "password";
        else if ((flow = scheme.Flows.AuthorizationCode) is not null) grant = "authorization_code";
        else if ((flow = scheme.Flows.Implicit) is not null) grant = "implicit";
        else return (null, new List<KvPair>());

        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["client_id"] = "{{client_id}}",
            ["client_secret"] = "{{client_secret}}",
        };
        if (flow.TokenUrl is not null) parameters["access_token_url"] = flow.TokenUrl.ToString();
        if (flow.AuthorizationUrl is not null) parameters["authorization_url"] = flow.AuthorizationUrl.ToString();
        if (flow.RefreshUrl is not null) parameters["refresh_token_url"] = flow.RefreshUrl.ToString();
        if (flow.Scopes is not null && flow.Scopes.Count > 0)
            parameters["scope"] = string.Join(" ", flow.Scopes.Keys);

        var vars = new List<KvPair>
        {
            new("client_id", string.Empty, true) { Description = "OAuth2 client id" },
            new("client_secret", string.Empty, true) { Description = "OAuth2 client secret" },
        };
        if (grant == "password")
        {
            parameters["username"] = "{{username}}";
            parameters["password"] = "{{password}}";
            vars.Add(new KvPair("username", string.Empty, true) { Description = "OAuth2 username" });
            vars.Add(new KvPair("password", string.Empty, true) { Description = "OAuth2 password" });
        }

        return (new AuthConfig { Type = AuthType.OAuth2, Parameters = parameters }, vars);
    }

    // ---------------- schema sampling ----------------

    /// <summary>Best-effort sample value from a JSON-Schema fragment. Uses example/default
    /// when present, otherwise generates a placeholder. For scalar params we return a
    /// short string; for objects we emit JSON. Walks allOf/oneOf/anyOf and guards against
    /// recursive schema references.</summary>
    private static string SampleFromSchema(OpenApiSchema? schema, HashSet<OpenApiSchema> visited, bool asObject = false)
    {
        if (schema is null) return string.Empty;
        if (!visited.Add(schema)) return asObject ? "{}" : string.Empty;
        try
        {
            if (schema.Example is not null)
                return schema.Example.ToString() ?? string.Empty;
            if (schema.Default is not null)
                return schema.Default.ToString() ?? string.Empty;

            // Composition: merge allOf properties; pick the first variant for oneOf/anyOf.
            var resolved = ResolveComposition(schema, visited);
            if (resolved is not null && !ReferenceEquals(resolved, schema))
                return SampleFromSchema(resolved, visited, asObject);

            if (asObject || schema.Type == "object" || (schema.Properties is not null && schema.Properties.Count > 0))
                return BuildObjectJson(schema, visited);

            return schema.Type switch
            {
                "integer" => "0",
                "number" => "0",
                "boolean" => "false",
                "array" => "[]",
                _ => string.Empty,
            };
        }
        finally
        {
            visited.Remove(schema);
        }
    }

    /// <summary>Reduces a composed schema (allOf merge / oneOf|anyOf first variant) to a
    /// flat shape sampling can iterate. Returns null when the schema has no composition.</summary>
    private static OpenApiSchema? ResolveComposition(OpenApiSchema schema, HashSet<OpenApiSchema> visited)
    {
        if (schema.AllOf is not null && schema.AllOf.Count > 0)
        {
            var merged = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal),
            };
            // Carry over own properties first.
            if (schema.Properties is not null)
                foreach (var (k, v) in schema.Properties) merged.Properties[k] = v;
            foreach (var member in schema.AllOf)
            {
                if (member is null || visited.Contains(member)) continue;
                if (member.Properties is null) continue;
                foreach (var (k, v) in member.Properties)
                    merged.Properties[k] = v;
            }
            return merged.Properties.Count == 0 ? null : merged;
        }
        if (schema.OneOf is not null && schema.OneOf.Count > 0)
        {
            var pick = schema.OneOf.FirstOrDefault(s => s is not null && !visited.Contains(s));
            if (pick is not null) return pick;
        }
        if (schema.AnyOf is not null && schema.AnyOf.Count > 0)
        {
            var pick = schema.AnyOf.FirstOrDefault(s => s is not null && !visited.Contains(s));
            if (pick is not null) return pick;
        }
        return null;
    }

    /// <summary>Returns a "one of: N variants" annotation when the schema uses oneOf or
    /// anyOf so the body sample's choice is discoverable in the request Docs.</summary>
    private static string DescribeComposition(OpenApiSchema? schema)
    {
        if (schema is null) return string.Empty;
        if (schema.OneOf is not null && schema.OneOf.Count > 1)
            return $"Body schema uses oneOf — first of {schema.OneOf.Count} variants shown.";
        if (schema.AnyOf is not null && schema.AnyOf.Count > 1)
            return $"Body schema uses anyOf — first of {schema.AnyOf.Count} variants shown.";
        return string.Empty;
    }

    private static string BuildObjectJson(OpenApiSchema schema, HashSet<OpenApiSchema> visited)
    {
        // If the schema is purely composed (no own Properties), drill into the composition
        // for property discovery.
        var source = schema;
        if ((source.Properties is null || source.Properties.Count == 0))
        {
            var composed = ResolveComposition(schema, visited);
            if (composed is not null) source = composed;
        }
        if (source.Properties is null || source.Properties.Count == 0) return "{}";

        var sb = new System.Text.StringBuilder();
        sb.Append("{\n");
        var i = 0;
        foreach (var (propName, propSchema) in source.Properties)
        {
            if (i > 0) sb.Append(",\n");
            sb.Append("  \"").Append(propName).Append("\": ");
            sb.Append(JsonValueFor(propSchema, visited));
            i++;
        }
        sb.Append("\n}");
        return sb.ToString();
    }

    private static string JsonValueFor(OpenApiSchema schema, HashSet<OpenApiSchema> visited)
    {
        if (schema is null) return "\"\"";
        if (!visited.Add(schema)) return "{}";
        try
        {
            if (schema.Example is not null) return "\"" + schema.Example.ToString() + "\"";
            // Drill through composition before falling to type-based defaults.
            var composed = ResolveComposition(schema, visited);
            var effective = composed ?? schema;

            return effective.Type switch
            {
                "integer" => "0",
                "number" => "0",
                "boolean" => "false",
                "array" => "[]",
                "object" => BuildObjectJson(effective, visited),
                _ => effective.Properties is not null && effective.Properties.Count > 0
                    ? BuildObjectJson(effective, visited)
                    : "\"\"",
            };
        }
        finally
        {
            visited.Remove(schema);
        }
    }

    private static List<KvPair> SampleFormFromSchema(OpenApiSchema? schema)
    {
        var result = new List<KvPair>();
        if (schema?.Properties is null) return result;
        foreach (var (k, v) in schema.Properties)
            result.Add(new KvPair(k, SampleFromSchema(v, new HashSet<OpenApiSchema>()), true));
        return result;
    }
}
