using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>One persisted tab entry — minimum needed to reopen on next launch.
/// <c>CollectionPath</c> ties the tab to its owning collection so the filtered tab strip can
/// show it only under the right scope; null = untagged (legacy entries / scope-less drafts).</summary>
public sealed record TabSessionEntry(
    string Id,
    string? SourcePath,
    string Name,
    string Kind,
    bool IsActive,
    string? CollectionPath = null);

/// <summary>JSON store for the open-tabs session, one file per workspace at
/// <c>%LocalAppData%/Vegha/tabs-{workspaceId}.json</c>. Stale entries (file deleted)
/// are silently dropped when the host re-hydrates.</summary>
public sealed class TabSessionStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public TabSessionStore(string workspaceId = "default")
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(workspaceId.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        _filePath = Path.Combine(dir, $"tabs-{safe}.json");
    }

    public IReadOnlyList<TabSessionEntry> Load()
    {
        if (!File.Exists(_filePath)) return Array.Empty<TabSessionEntry>();
        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<List<TabSessionEntry>>(json, s_json);
            return parsed ?? new List<TabSessionEntry>();
        }
        catch
        {
            return Array.Empty<TabSessionEntry>();
        }
    }

    public void Save(IReadOnlyList<TabSessionEntry> entries)
    {
        lock (_writeLock)
        {
            try
            {
                File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, s_json));
            }
            catch { /* best-effort */ }
        }
    }
}
