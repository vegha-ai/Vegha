using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.FileFormat;
using Vegha.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>Backs the top-bar workspace switcher.
///
/// A workspace is a Bruno-style folder containing many collections — on disk:
/// <c>&lt;FolderPath&gt;/workspace.yml</c>, <c>&lt;FolderPath&gt;/collections/&lt;name&gt;/</c>,
/// <c>&lt;FolderPath&gt;/environments/</c>. Activating a workspace clears the Collections
/// sidebar and loads every child of <c>collections/</c> as a separate root.
///
/// Per-collection expansion state is bucketed by absolute collection-root path so that
/// switching workspace restores each tree's open-folder set independently.</summary>
public partial class WorkspacesViewModel : ObservableObject
{
    private readonly WorkspaceStore _store;
    private readonly CollectionsViewModel _collections;
    private readonly ILogger<WorkspacesViewModel> _logger;
    private bool _suspendPersist;
    /// <summary>True while <see cref="ApplyActive"/> is clearing+restoring env state on a
    /// workspace switch. The store helpers short-circuit so the transient null we write to
    /// <c>_collections.ActiveGlobalEnvironment</c> mid-load doesn't get persisted onto the
    /// newly-activated workspace — which would erase its saved env selection before the
    /// restore step gets a chance to read it.</summary>
    private bool _isApplyingWorkspace;

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = new();

    [ObservableProperty]
    private WorkspaceItemViewModel? _activeWorkspace;

    public WorkspacesViewModel(
        WorkspaceStore store,
        CollectionsViewModel collections,
        ILogger<WorkspacesViewModel> logger)
    {
        _store = store;
        _collections = collections;
        _logger = logger;

        _collections.NodeExpansionChanged += OnNodeExpansionChanged;
        _collections.ActiveCollectionChanged += OnActiveCollectionChanged;
        _collections.PropertyChanged += (_, e) =>
        {
            // Persist per-collection env preference so each collection remembers what was
            // last selected even across workspace switches.
            if (e.PropertyName == nameof(CollectionsViewModel.ActiveEnvironment))
                StoreActiveEnvForCurrentCollection();
            // Persist workspace-level (global) env per workspace so each workspace remembers
            // its own selection independently. ApplyActive clears the slot before swapping
            // workspaces; this subscription rebuilds it as the user picks a new env.
            if (e.PropertyName == nameof(CollectionsViewModel.ActiveGlobalEnvironment))
                StoreActiveGlobalEnvForCurrentWorkspace();
        };

        // When a collection is removed from the tree (RemoveCollection / user Close), drop
        // it from the active workspace's LinkedCollections so the workspace.json no longer
        // tries to reload it on next launch. Without this the collection silently re-appears.
        _collections.Roots.CollectionChanged += OnCollectionsRootsChanged;

        // Bootstrap the default workspace (idempotent — a no-op once it exists).
        try { WorkspaceBootstrapper.EnsureDefaultWorkspace(_store); }
        catch (Exception ex) { _logger.LogWarning(ex, "Default workspace bootstrap failed"); }

        var state = _store.Load();
        _suspendPersist = true;
        try
        {
            foreach (var w in state.Workspaces)
            {
                var item = new WorkspaceItemViewModel(w.Name, w.FolderPath)
                {
                    IsDefault = w.IsDefault,
                    ActiveCollectionPath = w.ActiveCollectionPath,
                    ActiveGlobalEnvironmentName = w.ActiveGlobalEnvironmentName,
                };
                foreach (var (collectionPath, paths) in w.ExpandedPathsByCollection)
                    item.ExpandedPathsByCollection[collectionPath] =
                        new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                foreach (var linked in w.LinkedCollections)
                    item.LinkedCollections.Add(linked);
                foreach (var (k, v) in w.ActiveEnvironmentByCollection)
                    item.ActiveEnvironmentByCollection[k] = v;
                Workspaces.Add(item);
            }

            if (state.ActiveIndex >= 0 && state.ActiveIndex < Workspaces.Count)
                ActiveWorkspace = Workspaces[state.ActiveIndex];
            else
                ActiveWorkspace = Workspaces.FirstOrDefault(w => w.IsDefault) ?? Workspaces.FirstOrDefault();
        }
        finally { _suspendPersist = false; }

        // The first ApplyActive used to run synchronously in this ctor, which (since DI
        // resolves this VM as part of MainWindowViewModel) blocked the window from painting
        // until every collection's .bru tree had been walked + parsed. The host now calls
        // ApplyActiveAsync from MainWindow.OnLoaded at DispatcherPriority.Background, so
        // first paint happens immediately and the sidebar fills in afterwards. Subsequent
        // workspace switches still go through OnActiveWorkspaceChanged → ApplyActive
        // synchronously (the user-initiated path needs immediate visual feedback).
    }

    /// <summary>Public hook so the host can run the first <see cref="ApplyActive"/> at
    /// background dispatcher priority. Keeps the work on the UI thread (ApplyActive mutates
    /// UI-bound ObservableCollections) — running at <c>Background</c> just lets first paint
    /// happen first.</summary>
    public Task ApplyActiveAsync(WorkspaceItemViewModel ws)
    {
        ApplyActive(ws);
        return Task.CompletedTask;
    }

    partial void OnActiveWorkspaceChanged(WorkspaceItemViewModel? value)
    {
        if (_suspendPersist) return;
        if (value is not null) ApplyActive(value);
        Persist();
    }

    /// <summary>Loads every collection under <c>&lt;workspace&gt;/collections/</c> as a
    /// separate root in the Collections sidebar, and re-applies each tree's saved
    /// expansion set. Falls back to loading the workspace folder itself if no
    /// <c>collections/</c> subfolder exists (legacy single-folder workspaces).
    /// Also loads the workspace-level inheritance model (envs + scripts) and restores the
    /// per-workspace active collection.</summary>
    public void ApplyActive(WorkspaceItemViewModel ws)
    {
        _isApplyingWorkspace = true;
        try
        {
            // Capture the persisted active-collection path BEFORE we start loading.
            // Loading the first collection auto-selects it (CollectionsViewModel.OnRootsCollectionChanged),
            // which fires OnActiveCollectionChanged and overwrites ws.ActiveCollectionPath. Without
            // this snapshot the restore at the bottom would always land on the first collection.
            var persistedActivePath = ws.ActiveCollectionPath;

            _collections.Roots.Clear();
            _collections.Environments.Clear();
            _collections.ActiveCollection = null;
            // Drop any lingering ActiveEnvironment / ActiveGlobalEnvironment from the
            // previous workspace BEFORE loading the new envs. Without this, the previous
            // workspace's selected env name leaks into RebuildEnvironments and silently
            // auto-activates a same-named env in the new workspace — exactly the
            // "carry over" bug the per-workspace persistence is meant to prevent.
            _collections.ActiveEnvironment = null;
            _collections.ActiveGlobalEnvironment = null;
            if (!Directory.Exists(ws.FolderPath)) return;

            // Load workspace-level envs/scripts BEFORE collections so the first
            // ActiveCollection assignment can merge them into the env list.
            var wsModel = WorkspaceModelLoader.Load(ws.FolderPath);
            ws.WorkspaceModel = wsModel;
            _collections.SetWorkspaceEnvironments(wsModel.Environments.ToList());
            _collections.SetWorkspaceScripts(wsModel.PreRequestScript, wsModel.PostResponseScript, wsModel.TestsScript);

            // Restore the workspace's own persisted global env choice (null if it had
            // none, which is the right default for a fresh workspace).
            if (!string.IsNullOrEmpty(ws.ActiveGlobalEnvironmentName))
            {
                _collections.ActiveGlobalEnvironment = _collections.GlobalEnvironments
                    .FirstOrDefault(e => string.Equals(e.Name, ws.ActiveGlobalEnvironmentName, StringComparison.OrdinalIgnoreCase));
            }

            var collectionsRoot = Path.Combine(ws.FolderPath, "collections");
            if (Directory.Exists(collectionsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(collectionsRoot))
                {
                    _collections.LoadFromDirectory(dir);
                    if (ws.ExpandedPathsByCollection.TryGetValue(dir, out var set))
                        _collections.ApplyExpansionState(set);
                }
            }
            else
            {
                // Legacy workspace whose folder is itself a collection (pre-multi-collection
                // model). Load it directly. The expansion set is keyed by the workspace path.
                _collections.LoadFromDirectory(ws.FolderPath);
                if (ws.ExpandedPathsByCollection.TryGetValue(ws.FolderPath, out var set))
                    _collections.ApplyExpansionState(set);
            }

            // Linked collections — folders the user "Open Collection"-picked from outside the
            // workspace's collections/ folder. Persisted in workspaces.json so they survive
            // restarts. Skip silently if the folder no longer exists; we'll prune dead links
            // on the next persist.
            var pruned = new List<string>();
            foreach (var linked in ws.LinkedCollections.ToList())
            {
                if (!Directory.Exists(linked))
                {
                    pruned.Add(linked);
                    continue;
                }
                _collections.LoadFromDirectory(linked);
                if (ws.ExpandedPathsByCollection.TryGetValue(linked, out var set))
                    _collections.ApplyExpansionState(set);
            }
            if (pruned.Count > 0)
            {
                foreach (var p in pruned) ws.LinkedCollections.Remove(p);
                Persist();
            }

            // Restore the active collection from the snapshot we took before loading (the
            // path in ws.ActiveCollectionPath has been mutated by the auto-select cascade).
            // Falls back to the first available when the persisted path no longer resolves
            // (collection deleted or renamed on disk).
            if (!string.IsNullOrEmpty(persistedActivePath))
            {
                var match = _collections.AvailableCollections.FirstOrDefault(c =>
                    string.Equals(c.SourcePath, persistedActivePath, StringComparison.OrdinalIgnoreCase));
                _collections.ActiveCollection = match ?? _collections.AvailableCollections.FirstOrDefault();
            }
            else
            {
                _collections.ActiveCollection ??= _collections.AvailableCollections.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply workspace {Name} at {Path}", ws.Name, ws.FolderPath);
        }
        finally { _isApplyingWorkspace = false; }
    }

    /// <summary>Reacts to a collection switch: persist the new <c>ActiveCollectionPath</c> and
    /// restore the per-collection active environment from <see cref="WorkspaceItemViewModel.ActiveEnvironmentByCollection"/>.</summary>
    private void OnActiveCollectionChanged(object? sender, ActiveCollectionChangedEventArgs e)
    {
        if (ActiveWorkspace is null) return;
        ActiveWorkspace.ActiveCollectionPath = e.NewCollectionPath;

        if (!string.IsNullOrEmpty(e.NewCollectionPath)
            && ActiveWorkspace.ActiveEnvironmentByCollection.TryGetValue(e.NewCollectionPath, out var envName))
        {
            var match = _collections.Environments.FirstOrDefault(env =>
                string.Equals(env.Name, envName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) _collections.ActiveEnvironment = match;
        }

        if (!_suspendPersist) Persist();
    }

    private void StoreActiveEnvForCurrentCollection()
    {
        // Skip while ApplyActive is mid-flight — the transient nulls we write while loading
        // a workspace would otherwise overwrite the just-loaded persisted state.
        if (_isApplyingWorkspace) return;
        // Skip while CollectionsViewModel is rebuilding env lists. RebuildEnvironments fires
        // ActiveEnvironment PropertyChanged with ActiveCollection already swapped to the new
        // collection — persisting that "carry-over" value would clobber the new collection's
        // saved env name before OnActiveCollectionChanged below restores it.
        if (_collections.IsRebuildingEnvironments) return;
        if (ActiveWorkspace is null) return;
        var key = _collections.ActiveCollection?.SourcePath;
        if (string.IsNullOrEmpty(key)) return;

        var envName = _collections.ActiveEnvironment?.Name;
        if (string.IsNullOrEmpty(envName))
            ActiveWorkspace.ActiveEnvironmentByCollection.Remove(key);
        else
            ActiveWorkspace.ActiveEnvironmentByCollection[key] = envName;

        if (!_suspendPersist) Persist();
    }

    private void StoreActiveGlobalEnvForCurrentWorkspace()
    {
        // Same guard as above. Without it, ApplyActive's `_collections.ActiveGlobalEnvironment = null`
        // wipes the newly-activated workspace's saved env name before the restore line further
        // down can read it — losing every workspace's selection the moment another workspace
        // tries to set one.
        if (_isApplyingWorkspace) return;
        // Also skip during a RebuildEnvironments pass — the carry-over auto-activate
        // would otherwise persist mid-rebuild state.
        if (_collections.IsRebuildingEnvironments) return;
        if (ActiveWorkspace is null) return;
        ActiveWorkspace.ActiveGlobalEnvironmentName = _collections.ActiveGlobalEnvironment?.Name;
        if (!_suspendPersist) Persist();
    }

    /// <summary>Routes a tree-expansion toggle to the right per-collection bucket.
    /// We find the bucket by walking up <paramref name="e.Path"/> until we hit a key
    /// in <see cref="WorkspaceItemViewModel.ExpandedPathsByCollection"/> — the
    /// collection root the toggle belongs to.</summary>
    private void OnNodeExpansionChanged(object? sender, (string Path, bool Expanded) e)
    {
        if (ActiveWorkspace is null || string.IsNullOrEmpty(e.Path)) return;
        var bucket = ResolveBucket(ActiveWorkspace, e.Path);
        if (bucket is null) return;
        if (e.Expanded) bucket.Add(e.Path);
        else bucket.Remove(e.Path);
        Persist();
    }

    /// <summary>Keeps the active workspace's <c>LinkedCollections</c> in sync with the
    /// tree. When the user closes a collection, drop its persisted link so it doesn't
    /// silently re-appear on next launch.</summary>
    private void OnCollectionsRootsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_suspendPersist) return;
        if (ActiveWorkspace is null) return;
        if (e.OldItems is null) return;

        // Only act on genuine removals — a Replace action (ReloadRootContaining does
        // `Roots[i] = refreshed`) also fills OldItems, but the path is still loaded just
        // under a fresh node. Unlinking on Replace would silently erase the workspace's
        // link to a collection every time it refreshes on the watcher.
        var currentPaths = new HashSet<string>(
            _collections.Roots
                .OfType<CollectionRootViewModel>()
                .Select(r => r.SourcePath ?? string.Empty)
                .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase);

        bool changed = false;
        foreach (var removed in e.OldItems.OfType<CollectionRootViewModel>())
        {
            if (string.IsNullOrEmpty(removed.SourcePath)) continue;
            if (currentPaths.Contains(removed.SourcePath)) continue; // still loaded — refresh, not remove
            if (ActiveWorkspace.LinkedCollections.Remove(removed.SourcePath)) changed = true;
        }
        if (changed) Persist();
    }

    private static HashSet<string>? ResolveBucket(WorkspaceItemViewModel ws, string nodePath)
    {
        // Find the collection root that contains nodePath. The bucket is keyed by that root.
        foreach (var (collectionRoot, set) in ws.ExpandedPathsByCollection)
        {
            if (string.Equals(nodePath, collectionRoot, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(collectionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(collectionRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return set;
            }
        }

        // First toggle for this collection — derive the root by walking up to the workspace's
        // collections/ folder, then create a bucket for it.
        var collections = Path.Combine(ws.FolderPath, "collections");
        var probe = nodePath;
        while (!string.IsNullOrEmpty(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.Equals(parent, collections, StringComparison.OrdinalIgnoreCase))
            {
                var newSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ws.ExpandedPathsByCollection[probe] = newSet;
                return newSet;
            }
            if (string.IsNullOrEmpty(parent) || parent == probe) break;
            probe = parent;
        }

        // Legacy fallback — bucket keyed by the workspace folder itself.
        if (!ws.ExpandedPathsByCollection.TryGetValue(ws.FolderPath, out var legacy))
        {
            legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ws.ExpandedPathsByCollection[ws.FolderPath] = legacy;
        }
        return legacy;
    }

    /// <summary>Adds a new workspace pointing at <paramref name="folderPath"/>, activates it, and persists.
    /// If the folder does not yet contain a <c>workspace.yml</c>, this method does *not* write one —
    /// callers that want to convert an arbitrary folder into a workspace should use
    /// <see cref="CreateWorkspace"/> instead.</summary>
    public void AddWorkspace(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var name = ReadManifestName(folderPath) ?? DeriveDisplayName(folderPath);
        var item = new WorkspaceItemViewModel(name, folderPath);
        Workspaces.Add(item);
        ActiveWorkspace = item;
    }

    /// <summary>Records <paramref name="collectionFolder"/> as a linked collection in the
    /// active workspace, loads it into the Collections sidebar, and persists. Used by
    /// "Open Collection" — the folder may live anywhere on disk, not necessarily under
    /// <c>&lt;workspace&gt;/collections/</c>; persisting the path is the only way it'll
    /// reappear on app restart.</summary>
    public void LinkCollection(string collectionFolder)
    {
        if (string.IsNullOrWhiteSpace(collectionFolder)) return;
        var ws = ActiveWorkspace;
        if (ws is null) return;

        // No-op if it's already inside the workspace's collections/ folder — that's loaded
        // automatically on activation, no need to track it as a link.
        var collectionsRoot = Path.Combine(ws.FolderPath, "collections");
        var normalized = NormalizeForCompare(collectionFolder);
        var rootNormalized = NormalizeForCompare(collectionsRoot);
        var insideCollectionsRoot = normalized.StartsWith(
            rootNormalized + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

        _collections.LoadFromDirectory(collectionFolder);

        if (!insideCollectionsRoot && ws.LinkedCollections.Add(collectionFolder))
            Persist();
    }

    private static string NormalizeForCompare(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    /// <summary>Creates a new workspace folder at <paramref name="folderPath"/> with the given
    /// <paramref name="name"/>, writing <c>workspace.yml</c> + <c>collections/</c> +
    /// <c>environments/</c>, then registers and activates it.</summary>
    public void CreateWorkspace(string name, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(folderPath)) return;
        WorkspaceBootstrapper.EnsureFolderLayout(folderPath, name);
        var item = new WorkspaceItemViewModel(name, folderPath);
        Workspaces.Add(item);
        ActiveWorkspace = item;
    }

    [RelayCommand]
    private void RemoveWorkspace(WorkspaceItemViewModel? ws)
    {
        if (ws is null) return;
        if (ws.IsDefault) return; // Default workspace is permanent.
        var wasActive = ReferenceEquals(ws, ActiveWorkspace);
        Workspaces.Remove(ws);
        if (wasActive)
        {
            ActiveWorkspace = Workspaces.Count > 0 ? Workspaces[0] : null;
            if (ActiveWorkspace is null)
            {
                _collections.Roots.Clear();
                _collections.Environments.Clear();
            }
        }
        Persist();
    }

    private void Persist()
    {
        var state = new WorkspaceState
        {
            SchemaVersion = 4,
            Workspaces = Workspaces.Select(w => new Workspace(w.Name, w.FolderPath)
            {
                IsDefault = w.IsDefault,
                ExpandedPathsByCollection = w.ExpandedPathsByCollection
                    .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(),
                                  StringComparer.OrdinalIgnoreCase),
                LinkedCollections = w.LinkedCollections.ToList(),
                ActiveCollectionPath = w.ActiveCollectionPath,
                ActiveEnvironmentByCollection = new Dictionary<string, string>(
                    w.ActiveEnvironmentByCollection, StringComparer.OrdinalIgnoreCase),
                ActiveGlobalEnvironmentName = w.ActiveGlobalEnvironmentName,
            }).ToList(),
            ActiveIndex = ActiveWorkspace is null ? -1 : Workspaces.IndexOf(ActiveWorkspace),
        };
        _store.Save(state);
    }

    private static string? ReadManifestName(string folder)
    {
        try { return WorkspaceManifestIO.Read(folder)?.Name; }
        catch { return null; }
    }

    private static string DeriveDisplayName(string folderPath)
    {
        try
        {
            var name = new DirectoryInfo(folderPath).Name;
            return string.IsNullOrEmpty(name) ? folderPath : name;
        }
        catch { return folderPath; }
    }
}

/// <summary>Display row for a workspace in the switcher. Carries per-collection expansion sets
/// for the Collections tree; persisted by <see cref="WorkspacesViewModel"/>.</summary>
public partial class WorkspaceItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _folderPath;

    [ObservableProperty]
    private bool _isDefault;

    /// <summary>Per-collection expansion state. Key = absolute collection-root path,
    /// value = the set of expanded folder paths inside that collection.</summary>
    public Dictionary<string, HashSet<string>> ExpandedPathsByCollection { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute paths to collections that were "Open Collection"-picked from outside
    /// the workspace's own <c>collections/</c> folder. Persisted so the user's selections
    /// survive restarts; <see cref="WorkspacesViewModel.ApplyActive"/> loads each one
    /// in addition to the in-folder collections.</summary>
    public HashSet<string> LinkedCollections { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collection root path that was active when the user last left this workspace.
    /// Restored on activation so the user lands back on the same collection.</summary>
    public string? ActiveCollectionPath { get; set; }

    /// <summary>Per-collection memory of the active environment (collection root path → env name).</summary>
    public Dictionary<string, string> ActiveEnvironmentByCollection { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workspace-level (global) env that was active when the user last left this
    /// workspace. Set on every change via <see cref="WorkspacesViewModel"/>'s subscription
    /// to <c>CollectionsViewModel.ActiveGlobalEnvironment</c>. Per-workspace so the
    /// previous workspace's name never auto-applies in a different workspace.</summary>
    public string? ActiveGlobalEnvironmentName { get; set; }

    /// <summary>Loaded workspace-level inheritance payload (envs + scripts). Set by
    /// <see cref="WorkspacesViewModel.ApplyActive"/>; consumed by the request executor's
    /// merge chain. Not persisted — recomputed from disk on every activation.</summary>
    public Vegha.Core.FileFormat.WorkspaceModel WorkspaceModel { get; set; } =
        Vegha.Core.FileFormat.WorkspaceModel.Empty;

    public WorkspaceItemViewModel(string name, string folderPath)
    {
        _name = name;
        _folderPath = folderPath;
    }

    /// <summary>Two-letter monogram for the workspace badge (uses first two alphanumeric characters).</summary>
    public string Initials
    {
        get
        {
            var letters = Name.Where(char.IsLetterOrDigit).Take(2).ToArray();
            if (letters.Length == 0) return "?";
            return new string(letters).ToUpperInvariant();
        }
    }
}
