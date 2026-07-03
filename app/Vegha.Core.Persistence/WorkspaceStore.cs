using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>
/// JSON-backed registry of known workspaces at
/// <c>%LocalAppData%/Vegha/workspaces.json</c> (and platform equivalents).
/// Note: this file holds only the *registry* (which workspaces the app knows about,
/// the active index, per-collection expansion state). The workspace folders themselves
/// live elsewhere (default: <c>%AppData%/Roaming/Vegha/default-workspace</c>) and
/// each carries its own <c>workspace.yml</c>.
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public WorkspaceStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vegha", "workspaces.json"))
    {
    }

    /// <summary>Test-only ctor: lets tests redirect persistence to a temp file.</summary>
    public WorkspaceStore(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public WorkspaceState Load()
    {
        if (!File.Exists(_filePath)) return WorkspaceState.Empty;
        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<WorkspaceState>(json, s_jsonOptions);
            if (parsed is null) return WorkspaceState.Empty;
            return MaybeMigrate(parsed);
        }
        catch
        {
            return WorkspaceState.Empty;
        }
    }

    public void Save(WorkspaceState state)
    {
        lock (_writeLock)
        {
            try
            {
                // Force the current schema on every write so a default-constructed state
                // (e.g., from the bootstrapper before the VM rehydrates) doesn't leave a
                // schemaVersion: 0 file behind that would re-trigger migration next launch.
                var toSave = state with { SchemaVersion = 5 };
                var json = JsonSerializer.Serialize(toSave, s_jsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // best-effort
            }
        }
    }

    /// <summary>Chain of one-shot migrations:
    /// <list type="bullet">
    ///   <item>0/1 → 2: workspace-wide <c>ExpandedPaths</c> → per-collection bucket.</item>
    ///   <item>2 → 3: introduce <c>ActiveCollectionPath</c> + <c>ActiveEnvironmentByCollection</c>
    ///     (defaults — no on-disk reshuffle).</item>
    ///   <item>3 → 4: introduce <c>ActiveGlobalEnvironmentName</c> per workspace so the
    ///     workspace-level (global) env selection doesn't bleed across workspaces.</item>
    ///   <item>4 → 5: introduce <c>OpenCollectionPaths</c> per workspace (the capped, MRU
    ///     "open collections" set backing the picker + quick switcher).</item>
    /// </list>
    /// Each step is non-destructive: folders/files on disk are left untouched.</summary>
    private static WorkspaceState MaybeMigrate(WorkspaceState parsed)
    {
        var state = parsed;
        if (state.SchemaVersion < 2) state = MigrateToV2(state);
        if (state.SchemaVersion < 3) state = MigrateToV3(state);
        if (state.SchemaVersion < 4) state = MigrateToV4(state);
        if (state.SchemaVersion < 5) state = MigrateToV5(state);
        return state;
    }

    private static WorkspaceState MigrateToV2(WorkspaceState parsed)
    {
        var migrated = new List<Workspace>();
        foreach (var ws in parsed.Workspaces)
        {
            var bucket = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (ws.ExpandedPaths.Count > 0)
                bucket[ws.FolderPath] = ws.ExpandedPaths.ToList();

            migrated.Add(new Workspace(ws.Name, ws.FolderPath)
            {
                IsDefault = ws.IsDefault,
                ExpandedPathsByCollection = bucket,
                ExpandedPaths = Array.Empty<string>(),
            });
        }
        return new WorkspaceState
        {
            SchemaVersion = 2,
            Workspaces = migrated,
            ActiveIndex = parsed.ActiveIndex,
        };
    }

    private static WorkspaceState MigrateToV3(WorkspaceState parsed)
    {
        var migrated = parsed.Workspaces.Select(ws => new Workspace(ws.Name, ws.FolderPath)
        {
            IsDefault = ws.IsDefault,
            ExpandedPathsByCollection = ws.ExpandedPathsByCollection,
            ExpandedPaths = ws.ExpandedPaths,
            LinkedCollections = ws.LinkedCollections,
            ActiveCollectionPath = null,
            ActiveEnvironmentByCollection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        }).ToList();
        return new WorkspaceState
        {
            SchemaVersion = 3,
            Workspaces = migrated,
            ActiveIndex = parsed.ActiveIndex,
        };
    }

    private static WorkspaceState MigrateToV4(WorkspaceState parsed)
    {
        // Default each workspace's per-workspace active global env to null. There's no
        // sensible value to copy from v3 — the bleed across workspaces is exactly what
        // this field exists to prevent.
        var migrated = parsed.Workspaces.Select(ws => new Workspace(ws.Name, ws.FolderPath)
        {
            IsDefault = ws.IsDefault,
            ExpandedPathsByCollection = ws.ExpandedPathsByCollection,
            ExpandedPaths = ws.ExpandedPaths,
            LinkedCollections = ws.LinkedCollections,
            ActiveCollectionPath = ws.ActiveCollectionPath,
            ActiveEnvironmentByCollection = ws.ActiveEnvironmentByCollection,
            ActiveGlobalEnvironmentName = null,
        }).ToList();
        return new WorkspaceState
        {
            SchemaVersion = 4,
            Workspaces = migrated,
            ActiveIndex = parsed.ActiveIndex,
        };
    }

    private static WorkspaceState MigrateToV5(WorkspaceState parsed)
    {
        // Seed each workspace's open set with its last-active collection (if any) so the
        // picker's "Open collections" section isn't empty on first run after upgrade.
        var migrated = parsed.Workspaces.Select(ws => ws with
        {
            OpenCollectionPaths = string.IsNullOrEmpty(ws.ActiveCollectionPath)
                ? Array.Empty<string>()
                : new[] { ws.ActiveCollectionPath! },
        }).ToList();
        return new WorkspaceState
        {
            SchemaVersion = 5,
            Workspaces = migrated,
            ActiveIndex = parsed.ActiveIndex,
        };
    }
}
