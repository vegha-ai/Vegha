using System.Text.Json;

namespace Vegha.Core.Requests;

/// <summary>One "Additional Parameters" row of an OAuth2 config, as persisted. Kept separate
/// from <see cref="OAuth2AdditionalParam"/> (which is what the acquirer consumes) because the
/// persisted form also carries the enabled flag and may hold blank placeholder rows.</summary>
public sealed record AdditionalParamRow(
    string Key,
    string Value,
    string SendIn = "body",
    bool Enabled = true)
{
    public bool IsBlank => string.IsNullOrEmpty(Key) && string.IsNullOrEmpty(Value);
}

/// <summary>
/// Serialization for the parts of <see cref="Vegha.Core.Domain.AuthConfig.Parameters"/> that
/// aren't plain scalars. <c>Parameters</c> is a flat string→string dictionary so every existing
/// file format round-trips unchanged; the additional-parameter tables ride inside it as JSON.
/// </summary>
public static class AuthParameters
{
    /// <summary>Serializes rows for storage. Blank placeholder rows are UI chrome and are
    /// never persisted. An empty set serializes to <c>[]</c>.</summary>
    public static string SerializeAdditionalParams(IEnumerable<AdditionalParamRow> rows) =>
        JsonSerializer.Serialize(rows
            .Where(r => !r.IsBlank)
            .Select(r => new { key = r.Key, value = r.Value, sendIn = r.SendIn, enabled = r.Enabled }));

    /// <summary>Inverse of <see cref="SerializeAdditionalParams"/>. Tolerant of missing fields
    /// and malformed JSON — returns an empty list rather than throwing, so one bad entry can't
    /// take down the auth panel or a send.</summary>
    public static IReadOnlyList<AdditionalParamRow> DeserializeAdditionalParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<AdditionalParamRow>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Array.Empty<AdditionalParamRow>(); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<AdditionalParamRow>();

            var rows = new List<AdditionalParamRow>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                rows.Add(new AdditionalParamRow(
                    Key: Str(el, "key"),
                    Value: Str(el, "value"),
                    SendIn: Str(el, "sendIn") is { Length: > 0 } s ? s : "body",
                    // Absent → enabled, matching how every other KV surface treats a missing flag.
                    Enabled: !el.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False));
            }
            return rows;
        }
    }

    /// <summary>Narrows persisted rows to what the token acquirer sends: enabled, named rows.</summary>
    public static IReadOnlyList<OAuth2AdditionalParam> ToAcquirerParams(IEnumerable<AdditionalParamRow> rows) =>
        rows.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Key))
            .Select(r => new OAuth2AdditionalParam(r.Key, r.Value, r.SendIn))
            .ToList();

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
