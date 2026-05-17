using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>One recent collection entry — absolute path + the time it was last opened.</summary>
public sealed record RecentItem(string Path, DateTimeOffset LastOpenedUtc);

/// <summary>
/// Persists the user's recently opened collection paths under
/// <c>%LocalAppData%/Vegha/recent.json</c>. Capped at <see cref="MaxItems"/>; oldest
/// entries are evicted on push. Used by the welcome dialog and the File → Recent menu.
/// </summary>
public sealed class RecentItemsStore
{
    public const int MaxItems = 16;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public RecentItemsStore() : this(DefaultDir()) { }

    public RecentItemsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "recent.json");
    }

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha");

    public IReadOnlyList<RecentItem> Load()
    {
        if (!File.Exists(_filePath)) return Array.Empty<RecentItem>();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<RecentItem>>(json, s_json)
                ?? new List<RecentItem>();
        }
        catch { return Array.Empty<RecentItem>(); }
    }

    /// <summary>Records that <paramref name="path"/> was opened. If it's already in the list,
    /// moves it to the front and updates the timestamp; otherwise prepends and evicts the
    /// oldest. Persists to disk.</summary>
    public void Touch(string path, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        var when = now ?? DateTimeOffset.UtcNow;
        lock (_writeLock)
        {
            var current = Load().ToList();
            current.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            current.Insert(0, new RecentItem(path, when));
            if (current.Count > MaxItems) current.RemoveRange(MaxItems, current.Count - MaxItems);
            try { File.WriteAllText(_filePath, JsonSerializer.Serialize(current, s_json)); }
            catch { /* best-effort */ }
        }
    }

    public void Remove(string path)
    {
        lock (_writeLock)
        {
            var current = Load().ToList();
            if (current.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            try { File.WriteAllText(_filePath, JsonSerializer.Serialize(current, s_json)); }
            catch { /* best-effort */ }
        }
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            try { if (File.Exists(_filePath)) File.Delete(_filePath); }
            catch { /* best-effort */ }
        }
    }
}
