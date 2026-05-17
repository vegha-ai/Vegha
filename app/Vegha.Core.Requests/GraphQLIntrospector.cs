using System.Text;
using System.Text.Json;

namespace Vegha.Core.Requests;

/// <summary>
/// Introspects a GraphQL endpoint by POSTing the standard introspection query and
/// returning a flattened type/field list the UI can browse + use for autocomplete.
/// We deliberately don't pull in a full GraphQL client library — the introspection
/// shape is tiny and stable, so JsonDocument walking is sufficient.
/// </summary>
public static class GraphQLIntrospector
{
    /// <summary>The standard GraphQL introspection query — abridged to the parts the
    /// schema browser actually shows. Ignores directives + interface details.</summary>
    public const string IntrospectionQuery = """
        {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types {
              kind
              name
              description
              fields(includeDeprecated: false) {
                name
                description
                type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
                args { name type { kind name ofType { kind name } } }
              }
              enumValues(includeDeprecated: false) { name }
            }
          }
        }
        """;

    public sealed record GraphQLType(
        string Kind,
        string Name,
        string? Description,
        IReadOnlyList<GraphQLField> Fields,
        IReadOnlyList<string> EnumValues);

    public sealed record GraphQLField(
        string Name,
        string? Description,
        string TypeRef,
        IReadOnlyList<GraphQLArg> Args);

    public sealed record GraphQLArg(string Name, string TypeRef);

    public sealed record GraphQLSchema(
        string? QueryType,
        string? MutationType,
        string? SubscriptionType,
        IReadOnlyList<GraphQLType> Types);

    /// <summary>Sends the introspection query to the endpoint via the supplied executor and
    /// parses the response. <paramref name="headers"/> may carry auth — the caller is
    /// expected to pass the same headers a normal request would use.</summary>
    public static async Task<GraphQLSchema> IntrospectAsync(
        HttpExecutor executor,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var bodyJson = JsonSerializer.Serialize(new { query = IntrospectionQuery });
        var request = new HttpExecutionRequest(
            HttpMethod.Post, endpoint,
            Headers: headers,
            Body: bodyJson,
            ContentType: "application/json");
        var result = await executor.ExecuteAsync(request, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Introspection failed: HTTP {result.StatusCode} {result.ErrorMessage ?? result.ReasonPhrase}");
        return ParseSchema(result.Body);
    }

    /// <summary>Parses an introspection response body. Public for unit tests.</summary>
    public static GraphQLSchema ParseSchema(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("__schema", out var schema))
            throw new InvalidOperationException("Introspection response missing data.__schema");

        string? queryType = TryString(schema, "queryType", "name");
        string? mutationType = TryString(schema, "mutationType", "name");
        string? subscriptionType = TryString(schema, "subscriptionType", "name");

        var types = new List<GraphQLType>();
        if (schema.TryGetProperty("types", out var typesArr) && typesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesArr.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? string.Empty;
                if (name.StartsWith("__")) continue;  // skip introspection meta types

                var kind = t.GetProperty("kind").GetString() ?? "OBJECT";
                var desc = t.TryGetProperty("description", out var d) ? d.GetString() : null;

                var fields = new List<GraphQLField>();
                if (t.TryGetProperty("fields", out var fa) && fa.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fa.EnumerateArray())
                    {
                        var fname = f.GetProperty("name").GetString() ?? string.Empty;
                        var fdesc = f.TryGetProperty("description", out var fd) ? fd.GetString() : null;
                        var fref = FormatTypeRef(f.GetProperty("type"));

                        var args = new List<GraphQLArg>();
                        if (f.TryGetProperty("args", out var aa) && aa.ValueKind == JsonValueKind.Array)
                            foreach (var a in aa.EnumerateArray())
                                args.Add(new GraphQLArg(
                                    a.GetProperty("name").GetString() ?? string.Empty,
                                    FormatTypeRef(a.GetProperty("type"))));
                        fields.Add(new GraphQLField(fname, fdesc, fref, args));
                    }
                }

                var enumValues = new List<string>();
                if (t.TryGetProperty("enumValues", out var ea) && ea.ValueKind == JsonValueKind.Array)
                    foreach (var e in ea.EnumerateArray())
                        enumValues.Add(e.GetProperty("name").GetString() ?? string.Empty);

                types.Add(new GraphQLType(kind, name, desc, fields, enumValues));
            }
        }
        return new GraphQLSchema(queryType, mutationType, subscriptionType, types);
    }

    /// <summary>Renders a GraphQL type reference (handling NON_NULL + LIST wrappers) into
    /// the canonical <c>[Foo!]!</c> form.</summary>
    private static string FormatTypeRef(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Object) return "?";
        var kind = type.GetProperty("kind").GetString();
        var name = type.TryGetProperty("name", out var n) ? n.GetString() : null;
        switch (kind)
        {
            case "NON_NULL":
                return FormatTypeRef(type.GetProperty("ofType")) + "!";
            case "LIST":
                return "[" + FormatTypeRef(type.GetProperty("ofType")) + "]";
            default:
                return name ?? kind ?? "?";
        }
    }

    private static string? TryString(JsonElement parent, params string[] path)
    {
        var cur = parent;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out var next))
                return null;
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }
}
