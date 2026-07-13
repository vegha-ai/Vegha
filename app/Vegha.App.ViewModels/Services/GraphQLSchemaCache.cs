using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vegha.Core.GraphQL.Schema;

namespace Vegha.App.ViewModels.Services;

/// <summary>
/// Per-endpoint GraphQL schema cache: in-memory for the session, raw introspection JSON on
/// disk so schema features light up instantly (and offline) on the next app start. Disk
/// entries live at <c>%LocalAppData%/Vegha/graphql-schema-cache/{sha256(url)}.json</c> in a
/// small envelope (url + savedAt + raw). Never triggers network traffic — the "Introspect
/// schema" button remains the only thing that talks to the endpoint (local-first rule).
/// </summary>
public sealed class GraphQLSchemaCache
{
    private const int MaxDiskEntries = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, GraphQLSchemaModel> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;

    public GraphQLSchemaCache() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vegha", "graphql-schema-cache"))
    { }

    /// <summary>Test seam — point the cache at a temp directory.</summary>
    public GraphQLSchemaCache(string directory) => _directory = directory;

    /// <summary>Memory-first, then disk (parsed off the calling thread — call from a
    /// background task). Returns null on miss or unreadable entry; never throws.</summary>
    public async Task<GraphQLSchemaModel?> TryGetAsync(string endpointUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl)) return null;
        lock (_gate)
        {
            if (_memory.TryGetValue(endpointUrl, out var cached)) return cached;
        }
        try
        {
            var path = PathFor(endpointUrl);
            if (!File.Exists(path)) return null;
            var envelopeJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            using var envelope = JsonDocument.Parse(envelopeJson);
            // Guard against hash collisions / manual tampering: the envelope names its url.
            var storedUrl = envelope.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.Equals(storedUrl, endpointUrl, StringComparison.OrdinalIgnoreCase)) return null;
            if (!envelope.RootElement.TryGetProperty("raw", out var raw)) return null;
            var model = IntrospectionJsonReader.Parse(raw.GetString() ?? string.Empty);
            lock (_gate) { _memory[endpointUrl] = model; }
            return model;
        }
        catch
        {
            return null; // corrupt/stale cache entries silently miss
        }
    }

    /// <summary>Stores a freshly introspected schema (memory + disk, best-effort on disk).</summary>
    public async Task StoreAsync(
        string endpointUrl, string rawIntrospectionJson, GraphQLSchemaModel model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl)) return;
        lock (_gate) { _memory[endpointUrl] = model; }
        try
        {
            Directory.CreateDirectory(_directory);
            var envelope = JsonSerializer.Serialize(new
            {
                url = endpointUrl,
                savedAt = DateTimeOffset.UtcNow,
                raw = rawIntrospectionJson,
            });
            await File.WriteAllTextAsync(PathFor(endpointUrl), envelope, ct).ConfigureAwait(false);
            PruneDisk();
        }
        catch
        {
            // Disk cache is an optimization; the in-memory entry already serves this session.
        }
    }

    /// <summary>Drops the entry for an endpoint (both tiers). Used by "Refresh".</summary>
    public void Invalidate(string endpointUrl)
    {
        lock (_gate) { _memory.Remove(endpointUrl); }
        try
        {
            var path = PathFor(endpointUrl);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    private void PruneDisk()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.json");
        if (files.Length <= MaxDiskEntries) return;
        foreach (var stale in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxDiskEntries))
        {
            try { stale.Delete(); } catch { /* best-effort */ }
        }
    }

    private string PathFor(string endpointUrl)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(endpointUrl.ToLowerInvariant())));
        return Path.Combine(_directory, hash + ".json");
    }
}
