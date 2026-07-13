using System.Text.Json;

namespace Vegha.Core.GraphQL.Schema;

/// <summary>Thrown when an introspection response can't be turned into a schema —
/// carries the server's own error message when one was present (e.g. "introspection
/// is disabled") so the UI can show it verbatim.</summary>
public sealed class GraphQLIntrospectionException : Exception
{
    public GraphQLIntrospectionException(string message, bool serverRejected = false)
        : base(message) => ServerRejected = serverRejected;

    /// <summary>True when the server answered with a GraphQL <c>errors</c> array (a
    /// validation rejection worth retrying with a smaller query) rather than the
    /// response being structurally unusable.</summary>
    public bool ServerRejected { get; }
}

/// <summary>
/// Parses a raw introspection response into a <see cref="GraphQLSchemaModel"/>. Tolerant of
/// the reduced query variants — absent sections produce empty lists. Built to run off the
/// UI thread; multi-MB responses (GitHub-scale schemas) are the norm, not the exception.
/// </summary>
public static class IntrospectionJsonReader
{
    public static GraphQLSchemaModel Parse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // Servers commonly answer 200 + { errors: [...] } for rejected introspection —
        // check before data so the caller can fall back to a smaller query variant.
        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var msg = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new GraphQLIntrospectionException(
                msg ?? "The server returned GraphQL errors for the introspection query.",
                serverRejected: true);
        }

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("__schema", out var schema)
            || schema.ValueKind != JsonValueKind.Object)
        {
            throw new GraphQLIntrospectionException("Introspection response has no data.__schema.");
        }

        var types = new List<GraphQLTypeInfo>();
        if (schema.TryGetProperty("types", out var typesArr) && typesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesArr.EnumerateArray())
            {
                var name = Str(t, "name");
                if (string.IsNullOrEmpty(name) || name.StartsWith("__", StringComparison.Ordinal))
                    continue; // skip introspection meta types

                types.Add(new GraphQLTypeInfo(
                    name,
                    ParseKind(Str(t, "kind")),
                    Str(t, "description"),
                    ReadFields(t),
                    ReadInputValues(t, "inputFields"),
                    ReadEnumValues(t),
                    ReadTypeNameList(t, "interfaces"),
                    ReadTypeNameList(t, "possibleTypes")));
            }
        }

        var directives = new List<GraphQLDirectiveInfo>();
        if (schema.TryGetProperty("directives", out var dirArr) && dirArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in dirArr.EnumerateArray())
            {
                var name = Str(d, "name");
                if (string.IsNullOrEmpty(name)) continue;
                var locations = new List<string>();
                if (d.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
                    foreach (var l in locs.EnumerateArray())
                        if (l.GetString() is { } s) locations.Add(s);
                directives.Add(new GraphQLDirectiveInfo(
                    name, Str(d, "description"), locations, ReadInputValues(d, "args")));
            }
        }

        return new GraphQLSchemaModel(
            Str2(schema, "queryType", "name"),
            Str2(schema, "mutationType", "name"),
            Str2(schema, "subscriptionType", "name"),
            types,
            directives);
    }

    private static IReadOnlyList<GraphQLFieldInfo> ReadFields(JsonElement type)
    {
        if (!type.TryGetProperty("fields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<GraphQLFieldInfo>();
        var fields = new List<GraphQLFieldInfo>(arr.GetArrayLength());
        foreach (var f in arr.EnumerateArray())
        {
            var name = Str(f, "name");
            if (string.IsNullOrEmpty(name)) continue;
            fields.Add(new GraphQLFieldInfo(
                name,
                Str(f, "description"),
                ReadTypeRef(f, "type"),
                ReadInputValues(f, "args"),
                Bool(f, "isDeprecated"),
                Str(f, "deprecationReason")));
        }
        return fields;
    }

    private static IReadOnlyList<GraphQLArgInfo> ReadInputValues(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<GraphQLArgInfo>();
        var args = new List<GraphQLArgInfo>(arr.GetArrayLength());
        foreach (var a in arr.EnumerateArray())
        {
            var name = Str(a, "name");
            if (string.IsNullOrEmpty(name)) continue;
            args.Add(new GraphQLArgInfo(
                name, Str(a, "description"), ReadTypeRef(a, "type"), Str(a, "defaultValue")));
        }
        return args;
    }

    private static IReadOnlyList<GraphQLEnumValueInfo> ReadEnumValues(JsonElement type)
    {
        if (!type.TryGetProperty("enumValues", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<GraphQLEnumValueInfo>();
        var values = new List<GraphQLEnumValueInfo>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var name = Str(e, "name");
            if (string.IsNullOrEmpty(name)) continue;
            values.Add(new GraphQLEnumValueInfo(name, Str(e, "description"), Bool(e, "isDeprecated")));
        }
        return values;
    }

    private static IReadOnlyList<string> ReadTypeNameList(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var names = new List<string>(arr.GetArrayLength());
        foreach (var t in arr.EnumerateArray())
        {
            // interfaces/possibleTypes may come back as TypeRef trees; take the named root.
            var name = Str(t, "name") ?? ReadTypeRefTree(t).UnwrappedName;
            if (!string.IsNullOrEmpty(name)) names.Add(name!);
        }
        return names;
    }

    private static TypeRef ReadTypeRef(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var t) && t.ValueKind == JsonValueKind.Object
            ? ReadTypeRefTree(t)
            : TypeRef.Named("Unknown");

    private static TypeRef ReadTypeRefTree(JsonElement t)
    {
        var kind = Str(t, "kind");
        var name = Str(t, "name");
        var hasOf = t.TryGetProperty("ofType", out var of) && of.ValueKind == JsonValueKind.Object;
        return kind switch
        {
            "NON_NULL" => new TypeRef(TypeRefKind.NonNull, null, hasOf ? ReadTypeRefTree(of) : null),
            "LIST" => new TypeRef(TypeRefKind.List, null, hasOf ? ReadTypeRefTree(of) : null),
            _ => TypeRef.Named(name ?? "Unknown"),
        };
    }

    private static GraphQLTypeKind ParseKind(string? kind) => kind switch
    {
        "SCALAR" => GraphQLTypeKind.Scalar,
        "OBJECT" => GraphQLTypeKind.Object,
        "INTERFACE" => GraphQLTypeKind.Interface,
        "UNION" => GraphQLTypeKind.Union,
        "ENUM" => GraphQLTypeKind.Enum,
        "INPUT_OBJECT" => GraphQLTypeKind.InputObject,
        _ => GraphQLTypeKind.Unknown,
    };

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str2(JsonElement e, string prop1, string prop2) =>
        e.TryGetProperty(prop1, out var v) && v.ValueKind == JsonValueKind.Object
            ? Str(v, prop2) : null;

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
}
