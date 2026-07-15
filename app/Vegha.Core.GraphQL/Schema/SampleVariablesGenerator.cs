using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vegha.Core.GraphQL.Schema;

/// <summary>
/// Generates a JSON skeleton for an operation's variable definitions — the GraphQL analog
/// of <c>WsdlSampleEnvelopeGenerator</c>. Scalar defaults are neutral placeholders; unknown
/// (schema) types render as <c>{}</c> until schema-aware expansion lands.
/// </summary>
public static class SampleVariablesGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Builds a variables JSON object for <paramref name="variables"/>.
    /// Returns "{}" when there are none.</summary>
    public static string Generate(IReadOnlyList<GraphQLVariableInfo> variables)
    {
        var root = new JsonObject();
        foreach (var v in variables)
            root[v.Name] = SampleValue(v.TypeText);
        return root.ToJsonString(Indented);
    }

    /// <summary>Merges skeleton entries for any <paramref name="variables"/> missing from
    /// <paramref name="existingJson"/>, preserving everything the user already wrote.
    /// Returns null when nothing needed adding (or the existing JSON is unparseable).</summary>
    public static string? MergeMissing(string existingJson, IReadOnlyList<GraphQLVariableInfo> variables)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return null;
        }

        var added = false;
        foreach (var v in variables)
        {
            if (root.ContainsKey(v.Name)) continue;
            root[v.Name] = SampleValue(v.TypeText);
            added = true;
        }
        return added ? root.ToJsonString(Indented) : null;
    }

    private static JsonNode? SampleValue(string typeText)
    {
        // "[User!]!" → list of the inner type's sample; "!" only strips nullability.
        var t = typeText.TrimEnd('!');
        if (t.StartsWith('[') && t.EndsWith(']'))
            return new JsonArray();

        return t switch
        {
            "String" => JsonValue.Create(string.Empty),
            "ID" => JsonValue.Create(string.Empty),
            "Int" => JsonValue.Create(0),
            "Float" => JsonValue.Create(0.0),
            "Boolean" => JsonValue.Create(false),
            // Custom scalars / enums / input objects: an empty object is the least-wrong
            // placeholder for inputs; enums/custom scalars the user fills in anyway.
            _ => new JsonObject(),
        };
    }
}
