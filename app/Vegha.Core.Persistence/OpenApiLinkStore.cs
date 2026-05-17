using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>One linked OpenAPI spec on a collection. <see cref="Source"/> is either a URL
/// or an absolute file path; <see cref="LastSyncedAt"/> tracks when drift was last checked
/// so the panel can show a "stale spec" badge.</summary>
public sealed record OpenApiLink(
    string Name,
    string Source,
    DateTimeOffset? LastSyncedAt = null);

/// <summary>
/// Stores OpenAPI links per-collection at <c>&lt;collectionRoot&gt;/.vegha/openapi-links.json</c>
/// so the file commits with the collection (Git-friendly: shareable across teammates).
/// </summary>
public sealed class OpenApiLinkStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePathForCollection(string collectionRoot)
        => Path.Combine(collectionRoot, ".vegha", "openapi-links.json");

    public IReadOnlyList<OpenApiLink> Load(string collectionRoot)
    {
        var path = FilePathForCollection(collectionRoot);
        if (!File.Exists(path)) return Array.Empty<OpenApiLink>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<OpenApiLink>>(json, s_json)
                ?? new List<OpenApiLink>();
        }
        catch
        {
            return Array.Empty<OpenApiLink>();
        }
    }

    public void Save(string collectionRoot, IReadOnlyList<OpenApiLink> links)
    {
        var path = FilePathForCollection(collectionRoot);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try { File.WriteAllText(path, JsonSerializer.Serialize(links, s_json)); }
        catch { /* best-effort */ }
    }

    public void Add(string collectionRoot, OpenApiLink link)
    {
        var existing = Load(collectionRoot).ToList();
        // Merge by Source so re-adding the same spec just refreshes Name + LastSyncedAt.
        var idx = existing.FindIndex(l =>
            string.Equals(l.Source, link.Source, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) existing[idx] = link;
        else existing.Add(link);
        Save(collectionRoot, existing);
    }

    public void Remove(string collectionRoot, string source)
    {
        var existing = Load(collectionRoot).ToList();
        existing.RemoveAll(l => string.Equals(l.Source, source, StringComparison.OrdinalIgnoreCase));
        Save(collectionRoot, existing);
    }

    public void TouchSyncedAt(string collectionRoot, string source, DateTimeOffset when)
    {
        var existing = Load(collectionRoot).ToList();
        var idx = existing.FindIndex(l =>
            string.Equals(l.Source, source, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        existing[idx] = existing[idx] with { LastSyncedAt = when };
        Save(collectionRoot, existing);
    }
}
