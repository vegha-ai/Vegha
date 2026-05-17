using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports a Postman environment JSON export into a <see cref="Domain.Environment"/>.
/// Mirrors <c>bruno-converters/src/postman/postman-env-to-bruno-env.js</c>.
/// </summary>
public static class PostmanEnvironmentImporter
{
    public static Domain.Environment ImportFromFile(string path)
        => ImportFromString(File.ReadAllText(path));

    public static Domain.Environment ImportFromString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? "Untitled"
            : "Untitled";

        var variables = new List<KvPair>();
        var secret = new List<string>();

        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in values.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var key = entry.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                var value = entry.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;

                var enabled = !entry.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                var type = entry.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

                // Postman allows '.', spaces, etc. in env names; we normalize to underscores per Bruno's rules.
                var normalized = NormalizeName(key);
                variables.Add(new KvPair(normalized, value ?? string.Empty, enabled));
                if (string.Equals(type, "secret", StringComparison.OrdinalIgnoreCase))
                    secret.Add(normalized);
            }
        }

        return new Domain.Environment
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Variables = variables,
            SecretVariables = secret,
        };
    }

    private static readonly System.Text.RegularExpressions.Regex InvalidVariableChar =
        new(@"[^A-Za-z0-9_]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string NormalizeName(string key) => InvalidVariableChar.Replace(key, "_");
}
