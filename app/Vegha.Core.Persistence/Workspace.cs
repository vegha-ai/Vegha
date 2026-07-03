namespace Vegha.Core.Persistence;

/// <summary>
/// A user-defined workspace — a Bruno-style folder containing many collections.
/// On disk: <c>&lt;FolderPath&gt;/workspace.yml</c> + <c>&lt;FolderPath&gt;/collections/&lt;name&gt;/</c>
/// + <c>&lt;FolderPath&gt;/environments/</c>. The user switches between workspaces via the
/// top-bar dropdown.
/// </summary>
public sealed record Workspace(string Name, string FolderPath)
{
    /// <summary>True for the auto-bootstrapped default workspace at
    /// <c>%AppData%/Roaming/Vegha/default-workspace</c>. The Manage Workspaces
    /// dialog disables Remove for this row.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Per-collection expansion state, keyed by absolute collection-root path.
    /// Restored when the workspace activates. A workspace with N collections has N
    /// entries; switching workspace re-applies each entry to its matching root tree.</summary>
    public IDictionary<string, IReadOnlyList<string>> ExpandedPathsByCollection { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy single-set expansion paths, kept for one-shot upgrade migration only.
    /// New code should write <see cref="ExpandedPathsByCollection"/>; the store flushes this
    /// field after migration runs.</summary>
    public IReadOnlyList<string> ExpandedPaths { get; init; } = Array.Empty<string>();

    /// <summary>Absolute paths of collection folders that live OUTSIDE
    /// <c>&lt;FolderPath&gt;/collections/</c> but were added to this workspace via
    /// "Open Collection". Loaded alongside the in-folder collections on activation
    /// so the user's selections survive app restarts.</summary>
    public IReadOnlyList<string> LinkedCollections { get; init; } = Array.Empty<string>();

    /// <summary>The collection root path that was active when the user last left this
    /// workspace. Restored on activation so the user lands back on the same collection.</summary>
    public string? ActiveCollectionPath { get; init; }

    /// <summary>Per-collection memory of the active environment, keyed by absolute collection
    /// root path → environment name. When the user switches collections inside a workspace,
    /// the active env restores to whatever was last selected for that collection.</summary>
    public IDictionary<string, string> ActiveEnvironmentByCollection { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the workspace-level (global) environment that was active when the
    /// user last left this workspace. Restored on activation. Per-workspace so switching
    /// workspaces doesn't bleed one workspace's "active global env" into another that
    /// happens to share an env name.</summary>
    public string? ActiveGlobalEnvironmentName { get; init; }

    /// <summary>The workspace's currently-OPEN collections, newest-first, capped (max 5).
    /// Drives the "Open collections" section of the collection picker and the quick switcher.
    /// A collection joins this set when activated (moved to front, evicting the least-recent
    /// past the cap) and leaves it when the user closes it via the picker's ✕ — closing keeps
    /// the collection linked to the workspace (unlike Remove), so it can be reopened from the
    /// "All collections" section.</summary>
    public IReadOnlyList<string> OpenCollectionPaths { get; init; } = Array.Empty<string>();
}

/// <summary>Top-level persisted state for the workspace switcher.</summary>
public sealed record WorkspaceState
{
    /// <summary>Schema version. Current payloads write 3 (per-collection scope: adds
    /// <see cref="Workspace.ActiveCollectionPath"/> + <see cref="Workspace.ActiveEnvironmentByCollection"/>).
    /// Older JSON files predate this field — when missing, deserialization yields 0, which
    /// signals <see cref="WorkspaceStore.Load"/> to run the migration chain.</summary>
    public int SchemaVersion { get; init; }

    public List<Workspace> Workspaces { get; init; } = new();

    /// <summary>Index into <see cref="Workspaces"/>, or -1 for none.</summary>
    public int ActiveIndex { get; init; } = -1;

    public static WorkspaceState Empty { get; } = new();
}
