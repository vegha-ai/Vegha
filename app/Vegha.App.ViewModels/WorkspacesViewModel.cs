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
    /// <summary>True while <see cref="ApplyActiveAsync"/> is clearing+restoring env state on a
    /// workspace switch. The store helpers short-circuit so the transient null we write to
    /// <c>_collections.ActiveGlobalEnvironment</c> mid-load doesn't get persisted onto the
    /// newly-activated workspace — which would erase its saved env selection before the
    /// restore step gets a chance to read it.</summary>
    private bool _isApplyingWorkspace;

    /// <summary>Cancels the in-flight <see cref="ApplyActiveAsync"/> when a newer switch
    /// supersedes it. <see cref="_applyGeneration"/> is the monotonic stamp that lets a
    /// superseded continuation know it's no longer current and skip resetting shared flags.
    /// Both are touched only on the UI thread, so no synchronization is needed.</summary>
    private CancellationTokenSource? _applyCts;
    private int _applyGeneration;

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
                item.OpenCollectionPaths.AddRange(w.OpenCollectionPaths.Take(MaxOpenCollections));
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

    /// <summary>Activates a workspace without freezing the UI: clears the sidebar, loads the
    /// workspace-level inheritance model and every collection under
    /// <c>&lt;workspace&gt;/collections/</c> (plus linked collections) off the UI thread, and
    /// fills the sidebar progressively. The <em>active</em> collection (the one the TreeView
    /// renders) loads first so the user sees their tree as soon as possible; the rest fill the
    /// picker behind it. Falls back to loading the workspace folder itself when there's no
    /// <c>collections/</c> subfolder (legacy single-folder workspaces).
    ///
    /// Cancellable + generation-guarded so a rapid workspace switch supersedes an in-flight
    /// load: the older load adds nothing to the sidebar and doesn't reset the shared
    /// loading/applying flags out from under the newer one.</summary>
    public async Task ApplyActiveAsync(WorkspaceItemViewModel ws)
    {
        // Supersede any in-flight apply. Bump the generation, cancel the previous token, and
        // start a fresh one — all synchronously on the UI thread before the first await, so an
        // older continuation that resumes later sees a stale generation/cancelled token and
        // bails before mutating the sidebar.
        var generation = ++_applyGeneration;
        _applyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _applyCts = cts;
        var ct = cts.Token;

        _isApplyingWorkspace = true;
        _collections.IsLoading = true;
        // Suspend auto-select for the whole load so progressively-added roots only fill the
        // picker; we activate the persisted collection explicitly once. Ref-counted, so it
        // composes if this apply is superseded before its scope disposes.
        using var autoSelect = _collections.SuspendAutoSelect();
        try
        {
            // Snapshot the persisted active-collection path BEFORE loading — used to pick the
            // active-first collection and to restore selection at the end.
            var persistedActivePath = ws.ActiveCollectionPath;

            // Clear the previous workspace's tree + env state up front so the skeleton shows
            // immediately and stale collections vanish at once. Dropping ActiveEnvironment /
            // ActiveGlobalEnvironment here prevents the previous workspace's selected env name
            // from leaking into RebuildEnvironments and silently auto-activating a same-named
            // env in the new workspace.
            _collections.Roots.Clear();
            _collections.Environments.Clear();
            _collections.ActiveCollection = null;
            _collections.ActiveEnvironment = null;
            _collections.ActiveGlobalEnvironment = null;
            // Drop the previous workspace's global envs too. We're about to await the new
            // workspace-model load off-thread; without this the env picker would briefly show
            // the old workspace's global envs during that gap (the old synchronous path had no
            // such window). Republished below once the new model is loaded.
            _collections.SetWorkspaceEnvironments(System.Array.Empty<Vegha.Core.Domain.Environment>());
            if (!Directory.Exists(ws.FolderPath)) return;

            // Workspace-level envs/scripts — parse off the UI thread (file reads + JSON), then
            // publish on the UI thread so the first ActiveCollection assignment can merge them.
            var wsModel = await Task.Run(() => WorkspaceModelLoader.Load(ws.FolderPath), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            ws.WorkspaceModel = wsModel;
            _collections.SetWorkspaceEnvironments(wsModel.Environments.ToList());
            _collections.SetWorkspaceScripts(wsModel.PreRequestScript, wsModel.PostResponseScript, wsModel.TestsScript);

            // Restore the workspace's own persisted global env choice (null for a fresh workspace).
            if (!string.IsNullOrEmpty(ws.ActiveGlobalEnvironmentName))
            {
                _collections.ActiveGlobalEnvironment = _collections.GlobalEnvironments
                    .FirstOrDefault(e => string.Equals(e.Name, ws.ActiveGlobalEnvironmentName, StringComparison.OrdinalIgnoreCase));
            }

            // Build the ordered list of collection directories: in-folder collections (or the
            // legacy single-folder workspace) then linked collections, pruning dead links.
            var dirs = new List<string>();
            var collectionsRoot = Path.Combine(ws.FolderPath, "collections");
            if (Directory.Exists(collectionsRoot))
                dirs.AddRange(Directory.EnumerateDirectories(collectionsRoot));
            else
                dirs.Add(ws.FolderPath);

            var pruned = new List<string>();
            foreach (var linked in ws.LinkedCollections.ToList())
            {
                if (!Directory.Exists(linked)) { pruned.Add(linked); continue; }
                dirs.Add(linked);
            }
            if (pruned.Count > 0)
            {
                foreach (var p in pruned) ws.LinkedCollections.Remove(p);
                Persist();
            }

            // Active-first: load the collection that will become active before the rest so the
            // user's tree appears ASAP (the TreeView binds to ActiveCollection, not Roots).
            var targetDir = ResolveTargetDir(dirs, persistedActivePath);
            if (targetDir is not null)
            {
                var targetRoot = await _collections.LoadFromDirectoryAsync(targetDir, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;
                if (targetRoot is not null)
                {
                    _collections.ActiveCollection = targetRoot; // explicit (auto-select is suspended)
                    // Expand the active tree right away; the merged pass at the end re-applies
                    // it (idempotent) alongside every other collection's expansion.
                    if (ws.ExpandedPathsByCollection.TryGetValue(targetDir, out var tset))
                        _collections.ApplyExpansionState(tset);
                }
            }

            // Remaining collections fill the picker progressively (each parse off-thread).
            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) return;
                if (string.Equals(dir, targetDir, StringComparison.OrdinalIgnoreCase)) continue;
                await _collections.LoadFromDirectoryAsync(dir, ct).ConfigureAwait(true);
            }
            if (ct.IsCancellationRequested) return;

            // Restore expansion across every loaded collection in one pass. Paths are absolute
            // and unique per collection, so a single merged set expands each tree correctly
            // without collapsing the others (and is one tree walk instead of N).
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in ws.ExpandedPathsByCollection.Values)
                foreach (var p in set) merged.Add(p);
            if (merged.Count > 0) _collections.ApplyExpansionState(merged);

            // Final active-collection restore — covers the no-persisted-path case and the case
            // where the active-first target failed to load. Explicit assignment works while
            // auto-select is still suspended; the scope resumes it on exit.
            if (_collections.ActiveCollection is null)
            {
                var match = string.IsNullOrEmpty(persistedActivePath)
                    ? null
                    : _collections.AvailableCollections.FirstOrDefault(c =>
                        string.Equals(c.SourcePath, persistedActivePath, StringComparison.OrdinalIgnoreCase));
                _collections.ActiveCollection = match ?? _collections.AvailableCollections.FirstOrDefault();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer apply — expected, drop silently.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply workspace {Name} at {Path}", ws.Name, ws.FolderPath);
        }
        finally
        {
            // Only clear the shared flags if we're still the current generation. A superseded
            // apply resetting them would hide the skeleton / drop the persist-guard while the
            // newer apply is still loading.
            if (generation == _applyGeneration)
            {
                _isApplyingWorkspace = false;
                _collections.IsLoading = false;
            }
        }
    }

    partial void OnActiveWorkspaceChanged(WorkspaceItemViewModel? value)
    {
        if (_suspendPersist) return;
        // Persist the active index synchronously (cheap) so a crash mid-load still remembers
        // the chosen workspace. Expansion / active-collection get re-persisted by their own
        // event handlers as the async load completes.
        Persist();
        // Fire-and-forget the cancellable apply — it supersedes any in-flight load.
        if (value is not null) _ = ApplyActiveAsync(value);
    }

    /// <summary>Picks which collection directory should load first (and become active). Prefers
    /// the one matching the workspace's persisted active-collection path; otherwise the first
    /// directory so the user sees <em>some</em> tree quickly. Null when there are none.</summary>
    private static string? ResolveTargetDir(List<string> dirs, string? persistedActivePath)
    {
        if (dirs.Count == 0) return null;
        if (!string.IsNullOrEmpty(persistedActivePath))
        {
            var match = dirs.FirstOrDefault(d =>
                string.Equals(NormalizeForCompare(d), NormalizeForCompare(persistedActivePath), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return dirs[0];
    }

    /// <summary>Reacts to a collection switch: persist the new <c>ActiveCollectionPath</c> and
    /// restore the per-collection active environment from <see cref="WorkspaceItemViewModel.ActiveEnvironmentByCollection"/>.</summary>
    private void OnActiveCollectionChanged(object? sender, ActiveCollectionChangedEventArgs e)
    {
        if (ActiveWorkspace is null) return;
        ActiveWorkspace.ActiveCollectionPath = e.NewCollectionPath;
        // Activating a collection opens it (moves to front of the open set; the least-recent
        // beyond the cap is evicted — i.e. auto-closed).
        TouchOpenCollection(ActiveWorkspace, e.NewCollectionPath);

        if (!string.IsNullOrEmpty(e.NewCollectionPath)
            && ActiveWorkspace.ActiveEnvironmentByCollection.TryGetValue(e.NewCollectionPath, out var envName))
        {
            var match = _collections.Environments.FirstOrDefault(env =>
                string.Equals(env.Name, envName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) _collections.ActiveEnvironment = match;
        }

        if (!_suspendPersist) Persist();
    }

    /// <summary>Max simultaneously-open collections per workspace. Opening a 6th evicts the
    /// least-recently-used from the open set.</summary>
    public const int MaxOpenCollections = 5;

    /// <summary>Moves <paramref name="path"/> to the front of the workspace's open set
    /// (de-duplicating), trimming to <see cref="MaxOpenCollections"/>. No-op for empty paths.</summary>
    private static void TouchOpenCollection(WorkspaceItemViewModel ws, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ws.OpenCollectionPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        ws.OpenCollectionPaths.Insert(0, path);
        if (ws.OpenCollectionPaths.Count > MaxOpenCollections)
            ws.OpenCollectionPaths.RemoveRange(MaxOpenCollections,
                ws.OpenCollectionPaths.Count - MaxOpenCollections);
    }

    /// <summary>Closes a collection: removes it from the workspace's open set WITHOUT
    /// unlinking it (unlike <c>RemoveCollection</c>). The collection stays loaded and linked,
    /// so it reappears in the picker's "All collections" section and reopens instantly. If the
    /// closed collection was active, activates the next open one (or, if the open set empties,
    /// the first available collection — which reopens it). Persists.</summary>
    public void CloseCollection(string collectionPath)
    {
        var ws = ActiveWorkspace;
        if (ws is null || string.IsNullOrEmpty(collectionPath)) return;

        ws.OpenCollectionPaths.RemoveAll(p => string.Equals(p, collectionPath, StringComparison.OrdinalIgnoreCase));

        var wasActive = string.Equals(_collections.ActiveCollection?.SourcePath, collectionPath,
            StringComparison.OrdinalIgnoreCase);
        if (wasActive)
        {
            // Prefer the next still-open collection (MRU order); fall back to any available
            // collection (activating it re-opens it via OnActiveCollectionChanged).
            CollectionRootViewModel? next = null;
            foreach (var p in ws.OpenCollectionPaths)
            {
                next = _collections.AvailableCollections.FirstOrDefault(c =>
                    string.Equals(c.SourcePath, p, StringComparison.OrdinalIgnoreCase));
                if (next is not null) break;
            }
            next ??= _collections.AvailableCollections.FirstOrDefault(c =>
                !string.Equals(c.SourcePath, collectionPath, StringComparison.OrdinalIgnoreCase));
            _collections.ActiveCollection = next; // may be null when no collections remain
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

    /// <summary>Renames a workspace: writes the new name into <c>workspace.yml</c> (the folder
    /// itself is left alone — identity is by manifest), updates the VM, and persists
    /// <c>workspaces.json</c>. The persist is a direct <see cref="Persist"/> call — the old
    /// MainWindow implementation "forced" it by re-assigning <c>ActiveWorkspace</c> to itself,
    /// which the equality-checked setter turns into a no-op, so the registry kept the stale
    /// name until some other action happened to persist.</summary>
    public bool RenameWorkspace(WorkspaceItemViewModel ws, string newName)
    {
        newName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, ws.Name, StringComparison.Ordinal))
            return false;
        try
        {
            var manifest = WorkspaceManifestIO.Read(ws.FolderPath) ?? new WorkspaceManifest();
            WorkspaceManifestIO.Write(ws.FolderPath, manifest with { Name = newName });
            ws.Name = newName;
            Persist();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename workspace failed for {Path}", ws.FolderPath);
            return false;
        }
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
            SchemaVersion = 5,
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
                OpenCollectionPaths = w.OpenCollectionPaths.ToList(),
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

    /// <summary>Enumerates the collections in <paramref name="ws"/> WITHOUT loading them —
    /// the in-folder <c>collections/*</c> plus linked collections, dead paths pruned, display
    /// name from <c>collection.bru</c>'s <c>meta.name</c> (fallback: folder name). Used by the
    /// picker's "Other workspaces" section and the quick switcher to list collections in
    /// workspaces that aren't the active one (so we can't rely on loaded <c>Roots</c>).</summary>
    public IReadOnlyList<WorkspaceCollectionRef> EnumerateWorkspaceCollections(WorkspaceItemViewModel ws)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkspaceCollectionRef>();

        void TryAdd(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (!seen.Add(full)) return;
            result.Add(new WorkspaceCollectionRef(ws, full, TryReadCollectionName(full) ?? DeriveDisplayName(full)));
        }

        var collectionsRoot = Path.Combine(ws.FolderPath, "collections");
        if (Directory.Exists(collectionsRoot))
        {
            try { foreach (var d in Directory.EnumerateDirectories(collectionsRoot)) TryAdd(d); }
            catch { /* unreadable workspace folder — skip */ }
        }
        foreach (var linked in ws.LinkedCollections) TryAdd(linked);
        return result;
    }

    /// <summary>Reads <c>collection.bru</c>'s <c>meta.name</c> for a collection folder without
    /// parsing the whole tree. Returns null when the file is absent / unparseable / nameless.</summary>
    private static string? TryReadCollectionName(string collectionDir)
    {
        try
        {
            var bru = Path.Combine(collectionDir, "collection.bru");
            if (!File.Exists(bru)) return null;
            var doc = Vegha.Core.Bru.Parser.BruParser.Parse(File.ReadAllText(bru));
            var meta = doc.Blocks.OfType<Vegha.Core.Bru.Parser.DictBlock>().FirstOrDefault(b => b.Name == "meta");
            var name = (meta?.Pairs.FirstOrDefault(p => p.Name == "name")?.Value as Vegha.Core.Bru.Parser.StringValue)?.Text;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
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
    /// survive restarts; <see cref="WorkspacesViewModel.ApplyActiveAsync"/> loads each one
    /// in addition to the in-folder collections.</summary>
    public HashSet<string> LinkedCollections { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collection root path that was active when the user last left this workspace.
    /// Restored on activation so the user lands back on the same collection.</summary>
    public string? ActiveCollectionPath { get; set; }

    /// <summary>The workspace's currently-OPEN collections, newest-first, capped at
    /// <see cref="WorkspacesViewModel.MaxOpenCollections"/>. Drives the picker's "Open
    /// collections" section + the quick switcher. A collection joins on activation (moved to
    /// front, LRU evicted past the cap) and leaves when closed via the picker ✕.</summary>
    public List<string> OpenCollectionPaths { get; init; } = new();

    /// <summary>Per-collection memory of the active environment (collection root path → env name).</summary>
    public Dictionary<string, string> ActiveEnvironmentByCollection { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workspace-level (global) env that was active when the user last left this
    /// workspace. Set on every change via <see cref="WorkspacesViewModel"/>'s subscription
    /// to <c>CollectionsViewModel.ActiveGlobalEnvironment</c>. Per-workspace so the
    /// previous workspace's name never auto-applies in a different workspace.</summary>
    public string? ActiveGlobalEnvironmentName { get; set; }

    /// <summary>Loaded workspace-level inheritance payload (envs + scripts). Set by
    /// <see cref="WorkspacesViewModel.ApplyActiveAsync"/>; consumed by the request executor's
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

/// <summary>A lightweight reference to a collection in a (possibly non-active) workspace —
/// its owning workspace, on-disk path, and display name — resolved WITHOUT loading the
/// collection tree. Used by the collection picker's "Other workspaces" section and the quick
/// switcher.</summary>
public sealed record WorkspaceCollectionRef(WorkspaceItemViewModel Workspace, string Path, string Name);
