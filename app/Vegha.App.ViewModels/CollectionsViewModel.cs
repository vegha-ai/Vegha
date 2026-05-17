using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Microsoft.Extensions.Logging;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.ViewModels;

/// <summary>Backs the Collections sidebar panel.</summary>
public partial class CollectionsViewModel : ObservableObject
{
    private readonly RequestEditorViewModel _requestEditor;
    private readonly Vegha.App.ViewModels.Tabs.OpenTabsViewModel? _openTabs;
    private readonly Vegha.Core.Requests.HttpExecutor? _httpExecutor;
    private readonly Vegha.Core.Persistence.RecentItemsStore? _recentItems;
    private readonly ILogger<CollectionsViewModel> _logger;

    /// <summary>Live results of the most recent Run-collection / Run-folder invocation.
    /// The Collections panel surfaces these in a small dialog/list as the run progresses.</summary>
    public ObservableCollection<Vegha.Core.Flow.RequestRunResult> LastRunResults { get; } = new();

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>True while the host is deferring the initial workspace-tree load past
    /// first paint. The Collections panel binds a "Loading workspace…" skeleton to this
    /// so the sidebar isn't a blank rectangle during the deferred warm-up.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True when the sidebar's "No collection selected" copy should show — i.e.
    /// no collection is active AND the deferred startup load isn't in progress. Lets the
    /// loading skeleton and the empty-state share the same blank-sidebar slot without
    /// overlapping each other.</summary>
    public bool ShowNoCollectionEmptyState => ActiveCollection is null && !IsLoading;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowNoCollectionEmptyState));

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Active tree-filter text. Setting this re-runs <see cref="ApplyFilter"/>
    /// which marks every node with <c>IsVisibleByFilter</c>; the TreeView's container style
    /// hides non-matching rows. Empty string = show everything.</summary>
    [ObservableProperty]
    private string _filter = string.Empty;

    partial void OnFilterChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private CollectionItemViewModel? _selectedItem;

    [ObservableProperty]
    private DomainEnv? _activeEnvironment;

    /// <summary>Active workspace-level (global) environment. Distinct from
    /// <see cref="ActiveEnvironment"/> which is the active collection-level env. Both apply
    /// at request execution time — collection vars override global vars on name collision.</summary>
    [ObservableProperty]
    private DomainEnv? _activeGlobalEnvironment;

    /// <summary>The collection currently scoping the UI — sidebar tree, env dropdown,
    /// tab strip, secrets, git all bind to this. <c>null</c> when no collections are loaded.</summary>
    [ObservableProperty]
    private CollectionRootViewModel? _activeCollection;

    public ObservableCollection<CollectionNodeViewModel> Roots { get; } = new();

    /// <summary>Collection roots available to the picker. Mirrors <see cref="Roots"/> but
    /// only the <see cref="CollectionRootViewModel"/> entries so the ComboBox can use a
    /// strongly-typed item template without filtering each render.</summary>
    public ObservableCollection<CollectionRootViewModel> AvailableCollections { get; } = new();

    public ObservableCollection<DomainEnv> Environments { get; } = new();

    /// <summary>Just the active collection's envs (no workspace ones). The split env picker
    /// in the top bar binds the "Collection" tab here.</summary>
    public ObservableCollection<DomainEnv> CollectionEnvironments { get; } = new();

    /// <summary>Workspace-level (global) envs only. Bound by the "Global" tab of the env picker.</summary>
    public ObservableCollection<DomainEnv> GlobalEnvironments { get; } = new();

    /// <summary>Workspace-level inheritance envs (loaded from <c>&lt;workspace&gt;/environments/</c>).
    /// Merged into the visible <see cref="Environments"/> list under the active collection's
    /// envs — set by <see cref="WorkspacesViewModel"/> when a workspace activates.</summary>
    public IReadOnlyList<DomainEnv> WorkspaceEnvironments { get; private set; } = Array.Empty<DomainEnv>();

    /// <summary>Workspace-level pre-request / post-response / tests scripts merged underneath
    /// the collection layer at compose time. Set by <see cref="WorkspacesViewModel"/>.</summary>
    public string? WorkspacePreRequestScript { get; private set; }
    public string? WorkspacePostResponseScript { get; private set; }
    public string? WorkspaceTestsScript { get; private set; }

    /// <summary>Fires after <see cref="ActiveCollection"/> changes, carrying old + new collection
    /// root paths. <see cref="WorkspacesViewModel"/>, secrets, git, and tabs subscribe.</summary>
    public event EventHandler<ActiveCollectionChangedEventArgs>? ActiveCollectionChanged;

    private bool _suppressExpansionEvents;

    /// <summary>Fired when any non-leaf node toggles. <c>(Path, Expanded)</c>.
    /// WorkspacesViewModel listens to this to persist the per-workspace expansion set.</summary>
    public event EventHandler<(string Path, bool Expanded)>? NodeExpansionChanged;

    /// <summary>UI-thread sync context captured at construction. Watcher events fire on a
    /// thread-pool thread; we marshal back here before mutating Roots / raising property
    /// changes so Avalonia bindings don't see cross-thread updates.</summary>
    private readonly System.Threading.SynchronizationContext? _uiContext;

    /// <summary>Per-root file system watchers. The CollectionsViewModel listens for any
    /// .bru change under a loaded collection root and reloads that root in-place,
    /// preserving expansion state. Mirrors Bruno's chokidar-driven auto-refresh.</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-root debounce timers — Windows fires multiple change events for a single
    /// save (rename + rename-back, plus a Changed); 500 ms collapses them into one reload.</summary>
    private readonly Dictionary<string, System.Threading.Timer?> _watcherDebounce =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-root timestamp (UTC ticks) before which watcher events should be ignored.
    /// LoadFromDirectory sets this to "now + grace" so post-import file-handle settling
    /// (Windows fires Changed events for LastWrite/Size after Write returns) doesn't trigger
    /// a redundant ReloadRootContaining that would tear down the just-added root.</summary>
    private readonly Dictionary<string, DateTime> _watcherSuppressUntil =
        new(StringComparer.OrdinalIgnoreCase);

    public CollectionsViewModel(
        RequestEditorViewModel requestEditor,
        ILogger<CollectionsViewModel> logger,
        Vegha.App.ViewModels.Tabs.OpenTabsViewModel? openTabs = null,
        Vegha.Core.Requests.HttpExecutor? httpExecutor = null,
        Vegha.Core.Persistence.RecentItemsStore? recentItems = null)
    {
        _requestEditor = requestEditor;
        _openTabs = openTabs;
        _httpExecutor = httpExecutor;
        _recentItems = recentItems;
        _logger = logger;
        _uiContext = System.Threading.SynchronizationContext.Current;

        Roots.CollectionChanged += OnRootsCollectionChanged;
    }

    private void OnRootsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Mirror the CollectionRootViewModel subset into AvailableCollections so the picker
        // updates as collections are loaded / removed. Folders + requests aren't pickable.
        //
        // The Replace action is its own branch: ReloadRootContaining swaps a root in place
        // (Roots[i] = refreshed), and the naive Remove(old) + Add(new) below would append
        // the new instance at the bottom of the picker — and, because the old reference
        // disappears from the list, the Contains check at the end would fall through and
        // reset ActiveCollection to the first item. The symptom users see: "the collection
        // I just clicked moved to the bottom and another collection is active instead;
        // clicking it again loads it." Handle Replace with index-preserving updates.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            && e.OldItems is not null && e.NewItems is not null
            && e.OldItems.Count == e.NewItems.Count)
        {
            var activeNeedsRepoint = false;
            CollectionRootViewModel? newActive = null;
            for (var i = 0; i < e.OldItems.Count; i++)
            {
                if (e.OldItems[i] is not CollectionRootViewModel oldRoot) continue;
                var newRoot = e.NewItems[i] as CollectionRootViewModel;
                var idx = AvailableCollections.IndexOf(oldRoot);
                if (idx >= 0 && newRoot is not null) AvailableCollections[idx] = newRoot;
                else if (idx >= 0) AvailableCollections.RemoveAt(idx);
                else if (newRoot is not null && !AvailableCollections.Contains(newRoot))
                    AvailableCollections.Add(newRoot);

                if (ReferenceEquals(ActiveCollection, oldRoot))
                {
                    activeNeedsRepoint = true;
                    newActive = newRoot;
                }
            }
            if (activeNeedsRepoint) ActiveCollection = newActive ?? AvailableCollections.FirstOrDefault();
        }
        else
        {
            if (e.NewItems is not null)
                foreach (var n in e.NewItems.OfType<CollectionRootViewModel>())
                    if (!AvailableCollections.Contains(n)) AvailableCollections.Add(n);
            if (e.OldItems is not null)
                foreach (var n in e.OldItems.OfType<CollectionRootViewModel>())
                    AvailableCollections.Remove(n);
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                AvailableCollections.Clear();
                foreach (var n in Roots.OfType<CollectionRootViewModel>()) AvailableCollections.Add(n);
            }
        }

        // Auto-select first available when none active.
        if (ActiveCollection is null) ActiveCollection = AvailableCollections.FirstOrDefault();
        // If the active one got removed, fall back.
        else if (!AvailableCollections.Contains(ActiveCollection))
            ActiveCollection = AvailableCollections.FirstOrDefault();
    }

    /// <summary>Replaces the workspace-level env set. Called by <see cref="WorkspacesViewModel"/>
    /// after a workspace activates (loads <c>&lt;workspace&gt;/environments/</c>). Triggers a
    /// rebuild of the visible <see cref="Environments"/> list under the current scope.</summary>
    public void SetWorkspaceEnvironments(IReadOnlyList<DomainEnv> workspaceEnvs)
    {
        WorkspaceEnvironments = workspaceEnvs ?? Array.Empty<DomainEnv>();
        RebuildEnvironments();
    }

    /// <summary>Replaces the workspace-level pre-request / post-response / tests scripts.
    /// Called by <see cref="WorkspacesViewModel"/> on workspace activation; flows into the
    /// merge chain underneath the collection.</summary>
    public void SetWorkspaceScripts(string? preRequestScript, string? postResponseScript, string? testsScript)
    {
        WorkspacePreRequestScript = preRequestScript;
        WorkspacePostResponseScript = postResponseScript;
        WorkspaceTestsScript = testsScript;
        // Refresh inheritance hints on every open tab so "Inherited from workspace" labels update.
        if (_openTabs is not null)
        {
            foreach (var tab in _openTabs.Tabs.OfType<Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel>())
                tab.Editor.RefreshInheritanceHints();
        }
    }

    partial void OnActiveCollectionChanged(CollectionRootViewModel? oldValue, CollectionRootViewModel? newValue)
    {
        var oldPath = oldValue?.SourcePath;
        var newPath = newValue?.SourcePath;
        OnPropertyChanged(nameof(ShowNoCollectionEmptyState));
        RebuildEnvironments();

        // Defensive — a failing subscriber (git, secrets, workspace persist) must not unwind
        // through the property setter and leave the ComboBox in an inconsistent state.
        if (ActiveCollectionChanged is { } handler)
        {
            foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<ActiveCollectionChangedEventArgs>>())
            {
                try { subscriber(this, new ActiveCollectionChangedEventArgs(oldPath, newPath)); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ActiveCollectionChanged subscriber failed (collection={Path})", newPath);
                }
            }
        }

        try { PushEnvironmentToOpenTabs(); }
        catch (Exception ex) { _logger.LogWarning(ex, "PushEnvironmentToOpenTabs failed"); }
    }

    /// <summary>True while <see cref="RebuildEnvironments"/> is running. External listeners
    /// of <see cref="ActiveEnvironment"/> / <see cref="ActiveGlobalEnvironment"/> PropertyChanged
    /// can check this and skip side-effects that depend on the change being user-initiated —
    /// the rebuild's carry-over auto-activate fires PropertyChanged with the new (post-swap)
    /// ActiveCollection already in place, and any "persist to ActiveCollection.SourcePath
    /// bucket" logic would otherwise overwrite the new collection's saved state with the old
    /// collection's env name. WorkspacesViewModel uses this gate.</summary>
    public bool IsRebuildingEnvironments { get; private set; }

    /// <summary>Rebuilds the visible env lists. <see cref="Environments"/> is the merged
    /// (workspace + collection) list kept for back-compat. <see cref="CollectionEnvironments"/>
    /// and <see cref="GlobalEnvironments"/> are the per-scope lists the split picker binds to.
    /// Same-named entries in the merged list: collection wins (added second).</summary>
    private void RebuildEnvironments()
    {
        IsRebuildingEnvironments = true;
        try
        {
            var previousCollectionEnvName = ActiveEnvironment?.Name;
            var previousGlobalEnvName     = ActiveGlobalEnvironment?.Name;

            // Per-scope lists.
            CollectionEnvironments.Clear();
            if (ActiveCollection?.Collection is { } col)
                foreach (var env in col.Environments) CollectionEnvironments.Add(env);
            GlobalEnvironments.Clear();
            foreach (var env in WorkspaceEnvironments) GlobalEnvironments.Add(env);

            // Legacy merged list.
            Environments.Clear();
            foreach (var env in GlobalEnvironments) Environments.Add(env);
            foreach (var env in CollectionEnvironments) Environments.Add(env);

            ActiveEnvironment = !string.IsNullOrEmpty(previousCollectionEnvName)
                ? CollectionEnvironments.FirstOrDefault(e =>
                    string.Equals(e.Name, previousCollectionEnvName, StringComparison.OrdinalIgnoreCase))
                : null;
            ActiveGlobalEnvironment = !string.IsNullOrEmpty(previousGlobalEnvName)
                ? GlobalEnvironments.FirstOrDefault(e =>
                    string.Equals(e.Name, previousGlobalEnvName, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        finally { IsRebuildingEnvironments = false; }
    }

    /// <summary>Appends an imported environment and, when a collection is active, persists
    /// it to that collection's <c>environments/</c> folder so it survives restart and lives
    /// alongside the requests it pairs with. Without an active collection (e.g. user has no
    /// collections yet), falls back to in-memory only — the watcher reload will reconcile
    /// once a collection is loaded. Called by the Import wizard on Postman env confirm.</summary>
    public void AddEnvironment(DomainEnv environment)
    {
        var collectionRoot = ActiveCollection?.SourcePath;
        if (!string.IsNullOrEmpty(collectionRoot) && Directory.Exists(collectionRoot))
        {
            try { WriteEnvFileToCollection(collectionRoot, environment); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist imported env to collection {Path} failed", collectionRoot);
                // Fall through — still update the in-memory list below so the user sees it.
            }
        }

        CollectionEnvironments.Add(environment);
        Environments.Add(environment);
        if (ActiveEnvironment is null) ActiveEnvironment = environment;
    }

    /// <summary>Swaps an environment instance for a replacement (e.g. after a rename or color
    /// change) across every list the UI binds against: <see cref="CollectionEnvironments"/>,
    /// <see cref="GlobalEnvironments"/>, the back-compat union <see cref="Environments"/>,
    /// and the active-env references the top-bar pill displays. Records are immutable, so the
    /// caller passes a fresh instance built with <c>env with { … }</c> rather than mutating in
    /// place; without this swap the renamed env still appears under its old name in the bar
    /// because the bound instance never changed.</summary>
    /// <returns>True when the environment was found in at least one of the env <em>lists</em>
    /// — i.e. it is already tracked and the caller need not add it. The active-env reference
    /// updates do not count: a freshly-created env that has only been <em>activated</em> (so it
    /// is <see cref="ActiveGlobalEnvironment"/> but not yet in any list) must still report
    /// false so the caller appends it to the list.</returns>
    public bool ReplaceEnvironment(DomainEnv oldEnv, DomainEnv newEnv)
    {
        var inList = false;
        inList |= ReplaceInObservable(CollectionEnvironments, oldEnv, newEnv);
        inList |= ReplaceInObservable(GlobalEnvironments, oldEnv, newEnv);
        inList |= ReplaceInObservable(Environments, oldEnv, newEnv);

        if (WorkspaceEnvironments.Count > 0)
        {
            var idx = -1;
            for (var i = 0; i < WorkspaceEnvironments.Count; i++)
                if (SameEnv(WorkspaceEnvironments[i], oldEnv)) { idx = i; break; }
            if (idx >= 0)
            {
                var copy = WorkspaceEnvironments.ToList();
                copy[idx] = newEnv;
                WorkspaceEnvironments = copy;
                inList = true;
            }
        }

        // Keep the active-env pointers fresh, but this does not make the env "tracked" —
        // only list membership does.
        if (SameEnv(ActiveEnvironment, oldEnv)) ActiveEnvironment = newEnv;
        if (SameEnv(ActiveGlobalEnvironment, oldEnv)) ActiveGlobalEnvironment = newEnv;
        PushEnvironmentToOpenTabs();
        return inList;
    }

    /// <summary>True when two env references denote the same environment — the same instance,
    /// or the same stable <see cref="DomainEnv.Id"/>. Id-matching keeps the in-memory swap
    /// working even when the caller passes a fresh record (value semantics) rather than the
    /// exact tracked instance — which is what made a saved workspace env fail to propagate
    /// until an app restart.</summary>
    private static bool SameEnv(DomainEnv? candidate, DomainEnv target) =>
        candidate is not null &&
        (ReferenceEquals(candidate, target) ||
         (!string.IsNullOrEmpty(target.Id) && string.Equals(candidate.Id, target.Id, StringComparison.Ordinal)));

    private static bool ReplaceInObservable(ObservableCollection<DomainEnv> list, DomainEnv oldEnv, DomainEnv newEnv)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (SameEnv(list[i], oldEnv)) { list[i] = newEnv; return true; }
        }
        return false;
    }

    /// <summary>Writes one Bruno-style env file into <c>&lt;collectionRoot&gt;/environments/</c>.
    /// Mirrors <see cref="EnvironmentsViewModel.WriteSingleEnvFile"/>. With a collision suffix
    /// so re-importing an env of the same name doesn't silently overwrite the existing one.</summary>
    private static void WriteEnvFileToCollection(string collectionRoot, DomainEnv env)
    {
        var envDir = Path.Combine(collectionRoot, Vegha.Core.FileFormat.CollectionJson.EnvironmentsFolder);
        Directory.CreateDirectory(envDir);

        var baseName = SanitizeEnvFileName(env.Name);
        var suffix = Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix;
        var fileName = baseName + suffix;
        var fullPath = Path.Combine(envDir, fileName);
        for (var n = 2; File.Exists(fullPath); n++)
            fullPath = Path.Combine(envDir, $"{baseName}-{n}{suffix}");

        // Strip literal secret values into the encrypted sidecar before serializing.
        var store = new Vegha.Core.Persistence.EnvironmentSecretStore();
        var stripped = Vegha.Core.FileFormat.EnvironmentSecretSplitter.StripForPersistence(env, collectionRoot, store);
        var envFile = Vegha.Core.FileFormat.EnvironmentFile.FromDomain(stripped);
        File.WriteAllText(fullPath, Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(envFile));
    }

    private static string SanitizeEnvFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "imported" : s;
    }

    /// <summary>Walks <see cref="Roots"/> and sets <c>IsExpanded</c> on every non-leaf node based on
    /// whether its <see cref="CollectionNodeViewModel.Path"/> appears in <paramref name="expandedPaths"/>.
    /// Suppresses change notifications during the walk so the workspace store isn't rewritten.</summary>
    public void ApplyExpansionState(IReadOnlyCollection<string> expandedPaths)
    {
        var set = new HashSet<string>(expandedPaths, StringComparer.OrdinalIgnoreCase);
        _suppressExpansionEvents = true;
        try
        {
            foreach (var root in Roots) ApplyTo(root, set);
        }
        finally { _suppressExpansionEvents = false; }
    }

    private void ApplyTo(CollectionNodeViewModel node, HashSet<string> set)
    {
        if (!node.IsLeaf)
        {
            node.IsExpanded = set.Contains(node.Path);
            foreach (var child in node.Children) ApplyTo(child, set);
        }
    }

    private void HookExpansionEvents(CollectionNodeViewModel node)
    {
        if (node.IsLeaf) return;
        node.ExpansionChanged += OnNodeExpansionChanged;
        foreach (var child in node.Children) HookExpansionEvents(child);
    }

    private void OnNodeExpansionChanged(object? sender, bool expanded)
    {
        if (_suppressExpansionEvents) return;
        if (sender is CollectionNodeViewModel n)
            NodeExpansionChanged?.Invoke(this, (n.Path, expanded));
    }

    /// <summary>Loads a collection from disk and adds it as a root node. Records the path
    /// in the recent-items list so the welcome dialog and File → Recent menu can offer it.
    /// Idempotent: calling with a path that's already loaded as a root, or that lives
    /// underneath an existing root, is a no-op — the existing root's watcher will pick up
    /// any new contents on its own. Without this guard, an Import wizard write that lands
    /// inside an already-watched tree produces two roots over the same files, which then
    /// crash on tab open or expand.</summary>
    public void LoadFromDirectory(string rootDirectory)
    {
        try
        {
            var normalized = NormalizePath(rootDirectory);

            // Already loaded? Bail out — but reload it so the user sees fresh state if
            // disk has changed since.
            var existing = Roots.FirstOrDefault(r => SamePath(NormalizePath(r.Path), normalized));
            if (existing is not null)
            {
                ReloadRootContaining(existing);
                return;
            }

            // Under an existing root? The watcher there already covers it; adding a
            // second root over the same files causes drag/drop and tab session bookkeeping
            // to find two matches and pick wrong.
            if (Roots.Any(r => IsUnderneath(normalized, NormalizePath(r.Path))))
            {
                StatusMessage = $"'{Path.GetFileName(rootDirectory)}' is inside an already-loaded collection — refresh handled by watcher.";
                return;
            }

            var collection = CollectionLoader.Load(rootDirectory);
            var node = CollectionNodeViewModel.FromCollection(collection, rootDirectory);
            Roots.Add(node);
            HookExpansionEvents(node);

            // Newly-loaded collections become active immediately so the tree switches to
            // them. Without this the user has to manually pick the new collection in the
            // header dropdown after an Add/Open/Import.
            if (node is CollectionRootViewModel justAdded)
                ActiveCollection = justAdded;

            // Env list is now driven by ActiveCollection (set in OnRootsCollectionChanged when
            // the first root lands), so we no longer accumulate envs from every loaded
            // collection. RebuildEnvironments() runs as part of OnActiveCollectionChanged.

            _recentItems?.Touch(rootDirectory);

            // Watch the collection folder for any .bru / folder changes so the tree
            // refreshes automatically — same UX as Bruno's chokidar-driven sidebar.
            // The 2 s suppression window swallows the burst of Changed events Windows
            // emits as just-written files' metadata (LastWrite/Size) settles — without
            // it, ReloadRootContaining fires right after import and replaces the root,
            // which surfaces as "first click on the imported collection doesn't load."
            _watcherSuppressUntil[NormalizePath(rootDirectory)] = DateTime.UtcNow.AddSeconds(2);
            AttachFileSystemWatcher(rootDirectory);

            StatusMessage =
                $"Loaded '{collection.Name}' — {CountRequests(collection)} requests, {collection.Environments.Count} environments";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load collection from {Directory}", rootDirectory);
            StatusMessage = $"Failed: {ex.Message}";
        }
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return string.Empty;
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnderneath(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
        var rootWithSep = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Starts a recursive FileSystemWatcher on the collection root. Any .bru / folder
    /// change debounces 500 ms and reloads the affected root via <see cref="ReloadRootContaining"/>.
    /// Idempotent — calling for an already-watched path is a no-op.</summary>
    private void AttachFileSystemWatcher(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return;
        if (_watchers.ContainsKey(rootPath)) return;

        try
        {
            var w = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.CreationTime
                             | NotifyFilters.Size,
            };
            w.Created += (_, _) => ScheduleWatchReload(rootPath);
            w.Deleted += (_, _) => ScheduleWatchReload(rootPath);
            w.Changed += (_, _) => ScheduleWatchReload(rootPath);
            w.Renamed += (_, _) => ScheduleWatchReload(rootPath);
            w.Error += (_, e) => _logger.LogWarning(e.GetException(), "Watcher error on {Path}", rootPath);
            _watchers[rootPath] = w;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach file watcher to {Path}", rootPath);
        }
    }

    private void DetachFileSystemWatcher(string rootPath)
    {
        if (_watchers.TryGetValue(rootPath, out var w))
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* shutting down */ }
            _watchers.Remove(rootPath);
        }
        if (_watcherDebounce.TryGetValue(rootPath, out var t))
        {
            t?.Dispose();
            _watcherDebounce.Remove(rootPath);
        }
        _watcherSuppressUntil.Remove(NormalizePath(rootPath));
    }

    /// <summary>Marshals the reload back to the UI thread + debounces multi-event bursts.
    /// Honors <see cref="_watcherSuppressUntil"/> so events that fire in the grace window
    /// after a fresh LoadFromDirectory don't tear down the just-added root.</summary>
    private void ScheduleWatchReload(string rootPath)
    {
        var key = NormalizePath(rootPath);
        if (_watcherSuppressUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until)
            return;

        if (_watcherDebounce.TryGetValue(rootPath, out var existing))
            existing?.Dispose();

        _watcherDebounce[rootPath] = new System.Threading.Timer(
            _ =>
            {
                if (_uiContext is not null)
                    _uiContext.Post(__ => ReloadRootByPath(rootPath), null);
                else
                    ReloadRootByPath(rootPath);
            },
            state: null,
            dueTime: 500,
            period: System.Threading.Timeout.Infinite);
    }

    /// <summary>Finds the loaded root with the given source path and reloads it.</summary>
    private void ReloadRootByPath(string rootPath)
    {
        var root = Roots.OfType<CollectionRootViewModel>().FirstOrDefault(r =>
            string.Equals(r.SourcePath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (root is not null) ReloadRootContaining(root);
    }

    partial void OnActiveEnvironmentChanged(DomainEnv? value) => PushEnvironmentToOpenTabs();
    partial void OnActiveGlobalEnvironmentChanged(DomainEnv? value) => PushEnvironmentToOpenTabs();

    /// <summary>Pushes the merged environment snapshot to every open HTTP request tab.
    /// Merge order: global (workspace) env first, then collection env overrides on name
    /// collision — collection-level config wins because it's "closer" to the request.</summary>
    public void PushEnvironmentToOpenTabs()
    {
        var snapshot = SnapshotMerged(ActiveGlobalEnvironment, ActiveEnvironment);
        var secretNames = SnapshotMergedSecretNames(ActiveGlobalEnvironment, ActiveEnvironment);
        _requestEditor.EnvironmentVariables = snapshot;
        _requestEditor.SecretVariableNames = secretNames;

        if (_openTabs is null) return;
        foreach (var tab in _openTabs.Tabs.OfType<Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel>())
        {
            tab.Editor.EnvironmentVariables = snapshot;
            tab.Editor.SecretVariableNames = secretNames;
        }
    }

    /// <summary>Flattens an env's enabled variables into a name→value dict, deduping by
    /// last-write-wins so a misconfigured env with duplicate names still resolves cleanly.</summary>
    public static IReadOnlyDictionary<string, string> SnapshotEnv(DomainEnv? env) =>
        env is null
            ? new Dictionary<string, string>()
            : env.Variables
                .Where(v => v.Enabled && !string.IsNullOrEmpty(v.Name))
                .GroupBy(v => v.Name).Select(g => g.Last())
                .ToDictionary(v => v.Name, v => v.Value);

    /// <summary>Combines a global (workspace-level) env with a collection-level env into a
    /// single name→value dictionary, with collection vars overriding global vars on collision.</summary>
    public static IReadOnlyDictionary<string, string> SnapshotMerged(DomainEnv? global, DomainEnv? collection)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (global is not null)
            foreach (var v in global.Variables)
                if (v.Enabled && !string.IsNullOrEmpty(v.Name)) dict[v.Name] = v.Value;
        if (collection is not null)
            foreach (var v in collection.Variables)
                if (v.Enabled && !string.IsNullOrEmpty(v.Name)) dict[v.Name] = v.Value;
        return dict;
    }

    /// <summary>Names of variables flagged as secret in an environment — empty when null.</summary>
    public static IReadOnlyCollection<string> SnapshotSecretNames(DomainEnv? env) =>
        env?.SecretVariables.ToArray() ?? Array.Empty<string>();

    /// <summary>Union of the secret variable names across a global + collection env pair.</summary>
    public static IReadOnlyCollection<string> SnapshotMergedSecretNames(DomainEnv? global, DomainEnv? collection) =>
        (global?.SecretVariables ?? Enumerable.Empty<string>())
            .Concat(collection?.SecretVariables ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [RelayCommand]
    private void OpenRequest(CollectionItemViewModel? item)
    {
        if (item is null || item.Request is null) return;
        SelectedItem = item;

        // Preferred path: route through OpenTabs so each request gets its own tab.
        // Falls back to the legacy single-editor mutation when tabs aren't wired
        // (existing tests construct the VM directly without OpenTabs).
        if (_openTabs is not null)
        {
            var (collection, folderChain, collectionPath) = ResolveParentContextWithPath(item);
            var tab = _openTabs.OpenOrActivate(item.Request, item.SourcePath, collection, folderChain, collectionPath);
            // Seed the new tab's editor from the current ActiveEnvironment so {{var}}
            // resolution works immediately. We read direct from the env (not the legacy
            // singleton's snapshot) so the data is always fresh — important when the user
            // edited and saved the env between operations.
            if (tab is Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel httpTab
                && ActiveEnvironment is not null)
            {
                httpTab.Editor.EnvironmentVariables = SnapshotEnv(ActiveEnvironment);
                httpTab.Editor.SecretVariableNames = SnapshotSecretNames(ActiveEnvironment);
            }
        }
        else
        {
            _requestEditor.LoadFromRequestItem(item.Request, item.SourcePath);
        }
    }

    /// <summary>Walks the tree to find the Collection + Folder chain that contains the
    /// given request VM. Returns <c>(null, [])</c> when the item isn't actually anywhere
    /// in <see cref="Roots"/> (defensive — the OpenRequest caller hands in a tree node so
    /// this should always find a hit).</summary>
    public (Collection? Collection, IReadOnlyList<Folder> FolderChain) ResolveParentContext(CollectionItemViewModel item)
    {
        var (c, chain, _) = ResolveParentContextWithPath(item);
        return (c, chain);
    }

    /// <summary>Like <see cref="ResolveParentContext"/> but also returns the absolute collection
    /// root path so callers can stamp the resulting tab with its scope.</summary>
    public (Collection? Collection, IReadOnlyList<Folder> FolderChain, string? CollectionPath) ResolveParentContextWithPath(CollectionItemViewModel item)
    {
        foreach (var root in Roots)
        {
            if (root is not CollectionRootViewModel rootVm || rootVm.Collection is null) continue;
            var chain = new List<Folder>();
            if (FindIn(rootVm.Children, item, chain))
            {
                return (rootVm.Collection, chain, rootVm.SourcePath);
            }
        }
        return (null, Array.Empty<Folder>(), null);
    }

    private static bool FindIn(
        IEnumerable<CollectionNodeViewModel> nodes,
        CollectionItemViewModel target,
        List<Folder> chain)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (node is CollectionFolderViewModel folder)
            {
                if (folder.Folder is not null) chain.Add(folder.Folder);
                if (FindIn(folder.Children, target, chain)) return true;
                if (folder.Folder is not null) chain.RemoveAt(chain.Count - 1);
            }
        }
        return false;
    }

    // ============================== Context-menu commands ==============================
    // The tree row hover "..." button + right-click open the menu and bind to these commands.
    // All mutations go through CollectionStore via the active collection's root path so the
    // tree + on-disk state stay in sync.

    /// <summary>Reveals the node's file/folder in OS file explorer. Best-effort; silently no-ops
    /// if the path doesn't exist or the OS shell call fails.</summary>
    [RelayCommand]
    private void RevealInFileExplorer(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (System.OperatingSystem.IsWindows())
            {
                if (Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
                else if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (System.OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", File.Exists(path) ? $"-R \"{path}\"" : $"\"{path}\"");
            }
            else
            {
                var dir = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("xdg-open", dir);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Reveal failed for {Path}", path); }
    }

    /// <summary>Opens a system terminal at the node's folder.</summary>
    [RelayCommand]
    private void OpenInTerminal(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        var dir = string.IsNullOrEmpty(path) ? null : (Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path));
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (System.OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
            }
            else if (System.OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", $"-a Terminal \"{dir}\"");
            }
            else
            {
                System.Diagnostics.Process.Start("x-terminal-emulator", $"--working-directory=\"{dir}\"");
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "OpenInTerminal failed for {Dir}", dir); }
    }

    /// <summary>Collapses every non-leaf node in the tree (UI only, not persisted as a delete).</summary>
    [RelayCommand]
    private void CollapseAll(CollectionNodeViewModel? node)
    {
        void Walk(CollectionNodeViewModel n)
        {
            if (!n.IsLeaf) n.IsExpanded = false;
            foreach (var c in n.Children) Walk(c);
        }
        if (node is null) foreach (var r in Roots) Walk(r);
        else Walk(node);
    }

    [RelayCommand]
    private void RemoveCollection(CollectionRootViewModel? root)
    {
        if (root is null) return;
        if (!string.IsNullOrEmpty(root.SourcePath)) DetachFileSystemWatcher(root.SourcePath);
        Roots.Remove(root);
        StatusMessage = $"Closed “{root.Name}”. Files on disk are untouched.";
    }

    [RelayCommand]
    private async Task DeleteNodeAsync(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);

            // Remove from the tree.
            RemoveNodeFromParent(node!);
            StatusMessage = $"Deleted “{node!.Name}”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed for {Path}", path);
            StatusMessage = $"Delete failed: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CloneNode(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path) || node is null) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            if (File.Exists(path))
            {
                var ext = System.IO.Path.GetExtension(path);
                var stem = System.IO.Path.GetFileNameWithoutExtension(path);
                var copyName = NextUniqueName(dir, stem + " (copy)", ext);
                var dest = System.IO.Path.Combine(dir, copyName + ext);
                File.Copy(path, dest);
                // For .bru requests, rewrite the inner meta.name so the cloned tree node
                // shows the cloned name rather than the original. Best-effort — silently
                // skip on parse failure; the user can rename in place.
                if (string.Equals(ext, ".bru", StringComparison.OrdinalIgnoreCase))
                    TryRewriteBruMetaName(dest, copyName);
                StatusMessage = $"Cloned to “{copyName + ext}”.";
                ReloadRootContaining(node);
            }
            else if (Directory.Exists(path))
            {
                var parent = System.IO.Path.GetDirectoryName(path) ?? dir;
                var leafName = System.IO.Path.GetFileName(path);
                var copyName = NextUniqueName(parent, leafName + " (copy)", string.Empty);
                var dest = System.IO.Path.Combine(parent, copyName);
                CopyDirectoryRecursive(path, dest);
                StatusMessage = $"Cloned folder to “{copyName}”.";
                ReloadRootContaining(node);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clone failed for {Path}", path);
            StatusMessage = $"Clone failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RenameNodeAsync(CollectionNodeViewModel? node)
    {
        // Pure rename: new name comes from `node.Name` (the user already edited the bound TextBox
        // when this command fires, OR the host shows a small inline rename UI). In the absence of
        // an inline editor in the first cut, the host opens a NameInput dialog to update node.Name
        // before calling this command. Here we just persist the rename.
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path) || node is null) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            if (File.Exists(path))
            {
                var ext = System.IO.Path.GetExtension(path);
                var dest = System.IO.Path.Combine(dir, Sanitize(node.Name) + ext);
                if (!string.Equals(path, dest, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(path, dest);
                    StatusMessage = $"Renamed to “{node.Name}”.";
                    ReloadParentRoot(node);
                }
            }
            else if (Directory.Exists(path))
            {
                var parent = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent)) return;
                var dest = System.IO.Path.Combine(parent, Sanitize(node.Name));
                if (!string.Equals(path, dest, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(path, dest);
                    // CollectionLoader reads folder.bru's meta.name in preference to the
                    // directory name, so a stale meta.name would survive the rename and
                    // the tree would re-display the old label. Patch the meta block in
                    // place. Missing folder.bru is fine — the directory name is then
                    // authoritative for the loader.
                    UpdateFolderBruMetaName(dest, node.Name);
                    StatusMessage = $"Renamed folder to “{node.Name}”.";
                    ReloadParentRoot(node);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename failed for {Path}", path);
            StatusMessage = $"Rename failed: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    /// <summary>Raised when the user picks "New Request" on a folder/collection. The host
    /// (MainWindow) catches this, opens the New Request dialog, and on confirm calls
    /// <see cref="CreateRequestFromDialog"/> to actually write the file.</summary>
    public event EventHandler<CollectionNodeViewModel>? NewRequestRequested;

    [RelayCommand]
    private void CreateRequest(CollectionNodeViewModel? folderOrRoot)
    {
        if (folderOrRoot is null) { StatusMessage = "Select a folder first."; return; }
        // Route through the host so the user gets the New Request dialog (type / name / URL).
        // If no host is wired (tests), fall back to the legacy quick-create.
        if (NewRequestRequested is { } handler)
        {
            handler(this, folderOrRoot);
            return;
        }

        var dir = ResolveNodeDirectoryPath(folderOrRoot);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var name = NextUniqueName(dir, "new-request", ".bru");
            var path = System.IO.Path.Combine(dir, name + ".bru");
            File.WriteAllText(path,
                "meta {\n  name: " + name + "\n  type: http\n  seq: 1\n}\n\n" +
                "get {\n  url: https://example.com/\n}\n");
            StatusMessage = $"Created “{name}.bru”.";
            ReloadRootContaining(folderOrRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateRequest failed under {Dir}", dir);
            StatusMessage = $"Create request failed: {ex.Message}";
        }
    }

    /// <summary>Persists a new request from the New Request dialog. Writes a .bru with the
    /// chosen kind / name / method / URL into the target folder, refreshes the tree, and
    /// returns the new file path so the host can open it in a tab. <paramref name="curlCommand"/>
    /// is honored when <paramref name="kind"/> is <see cref="NewRequestKind.FromCurl"/> — the
    /// importer parses the command and emits the .bru.</summary>
    public string? CreateRequestFromDialog(
        CollectionNodeViewModel folderOrRoot,
        NewRequestKind kind,
        string name,
        string method,
        string url,
        string curlCommand)
    {
        var dir = ResolveNodeDirectoryPath(folderOrRoot);
        if (string.IsNullOrEmpty(dir)) { StatusMessage = "No target folder."; return null; }

        try
        {
            // From cURL: delegate to the importer and write its output as a .bru.
            if (kind == NewRequestKind.FromCurl)
            {
                if (string.IsNullOrWhiteSpace(curlCommand))
                { StatusMessage = "Paste a cURL command first."; return null; }
                var imported = ParseCurlCommand(curlCommand);
                if (imported is null) { StatusMessage = "Couldn't parse the cURL command."; return null; }
                var fallbackName = imported.Name;
                if (string.IsNullOrWhiteSpace(fallbackName))
                    fallbackName = "from-curl";
                var stem = string.IsNullOrWhiteSpace(name) ? fallbackName : name;
                stem = Sanitize(stem);
                var fileName = NextUniqueName(dir, stem, ".bru");
                var path = System.IO.Path.Combine(dir, fileName + ".bru");
                // BruEmitter expects the meta name to match the file stem so the imported tree
                // node renders the right label.
                var withName = imported with { Name = fileName };
                File.WriteAllText(path, Vegha.Core.Importers.BruEmitter.Emit(withName));
                StatusMessage = $"Imported cURL → {fileName}.bru";
                ReloadRootContaining(folderOrRoot);
                return path;
            }

            // Native kinds: build a minimal .bru with the chosen meta + URL.
            var stemName = string.IsNullOrWhiteSpace(name) ? "new-request" : Sanitize(name);
            var unique = NextUniqueName(dir, stemName, ".bru");
            var dest = System.IO.Path.Combine(dir, unique + ".bru");
            var bru = BuildMinimalBru(kind, unique, method, url);
            File.WriteAllText(dest, bru);
            StatusMessage = $"Created “{unique}.bru”.";
            ReloadRootContaining(folderOrRoot);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateRequestFromDialog failed under {Dir}", dir);
            StatusMessage = $"Create failed: {ex.Message}";
            return null;
        }
    }

    private static string BuildMinimalBru(NewRequestKind kind, string name, string method, string url)
    {
        // Map dialog kind to the meta.type Bruno expects.
        var metaType = kind switch
        {
            NewRequestKind.GraphQL   => "graphql",
            NewRequestKind.WebSocket => "ws",
            NewRequestKind.Grpc      => "grpc",
            NewRequestKind.Soap      => "soap",
            _                        => "http",
        };
        var verb = string.IsNullOrWhiteSpace(method) ? "get" : method.Trim().ToLowerInvariant();
        var safeUrl = string.IsNullOrWhiteSpace(url) ? "https://example.com/" : url.Trim();
        // For non-HTTP kinds we still emit a {verb} block to keep the file valid against the
        // Bruno parser; the workspace VM picks the right surface based on meta.type.
        return
            "meta {\n" +
            $"  name: {name}\n" +
            $"  type: {metaType}\n" +
            "  seq: 1\n" +
            "}\n\n" +
            $"{verb} {{\n" +
            $"  url: {safeUrl}\n" +
            "}\n";
    }

    [RelayCommand]
    private void CreateFolder(CollectionNodeViewModel? folderOrRoot)
    {
        // The header / tree menu's "New Folder" entry now opens a name-prompt dialog from
        // CollectionsPanel and calls CreateNamedFolder directly. This auto-naming entry
        // point is retained for any programmatic caller that doesn't want a UI prompt.
        var dir = ResolveNodeDirectoryPath(folderOrRoot);
        if (string.IsNullOrEmpty(dir)) return;
        var name = NextUniqueName(dir, "new-folder", string.Empty);
        CreateNamedFolder(folderOrRoot, name);
    }

    /// <summary>Creates a named subfolder under the target collection / folder and drops a
    /// minimal <c>folder.bru</c> marker so CollectionLoader keeps the (otherwise empty)
    /// folder visible — without that marker the loader's "skip empty dir" guard would
    /// silently drop the brand-new folder, leaving the user staring at an unchanged tree.
    /// Collides with an existing same-named folder by appending a numeric suffix.</summary>
    public void CreateNamedFolder(CollectionNodeViewModel? folderOrRoot, string requestedName)
    {
        var dir = ResolveNodeDirectoryPath(folderOrRoot);
        if (string.IsNullOrEmpty(dir)) return;
        var sanitized = SanitizeFolderName(requestedName);
        if (string.IsNullOrEmpty(sanitized)) return;

        try
        {
            // If the user-supplied name already exists on disk, append a numeric suffix so
            // the create never silently overwrites or fails.
            var finalName = Directory.Exists(System.IO.Path.Combine(dir, sanitized))
                ? NextUniqueName(dir, sanitized, string.Empty)
                : sanitized;

            var folderPath = System.IO.Path.Combine(dir, finalName);
            Directory.CreateDirectory(folderPath);
            // folder.bru marker — preserves the on-disk display name (which may differ
            // from the disk basename if the user picked characters we had to sanitize)
            // and keeps the loader's empty-folder skip guard happy.
            var bru = $"meta {{\n  name: {requestedName.Trim()}\n  type: folder\n  seq: 1\n}}\n";
            File.WriteAllText(System.IO.Path.Combine(folderPath, "folder.bru"), bru);

            StatusMessage = $"Created folder “{requestedName.Trim()}”.";
            ReloadParentRoot(folderOrRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateNamedFolder failed under {Dir}", dir);
            StatusMessage = $"Create folder failed: {ex.Message}";
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var s = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s;
    }

    [RelayCommand]
    private void CreateJsFile(CollectionNodeViewModel? folderOrRoot)
    {
        var dir = ResolveNodeDirectoryPath(folderOrRoot);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var name = NextUniqueName(dir, "helpers", ".js");
            var path = System.IO.Path.Combine(dir, name + ".js");
            File.WriteAllText(path, "// Shared script helpers — accessible to pre-request and tests scripts.\n");
            StatusMessage = $"Created “{name}.js”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateJsFile failed under {Dir}", dir);
            StatusMessage = $"Create JS file failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunCollectionAsync(CollectionRootViewModel? root)
    {
        if (root?.Collection is null) return;
        await ExecuteRunAsync(executeAsync =>
            Vegha.Core.Flow.CollectionRunner.RunAsync(root.Collection, executeAsync, default,
                onProgress: r => RunOnUi(() => LastRunResults.Add(r))));
    }

    [RelayCommand]
    private async Task RunFolderAsync(CollectionFolderViewModel? folderVm)
    {
        if (folderVm?.Folder is null) return;
        // Find the outer chain (folders above this one) so RunFolderAsync can prepend them
        // when calling the per-request executor (so inheritance still works).
        var (collection, outerChain) = ResolveFolderChain(folderVm);
        if (collection is null) return;
        await ExecuteRunAsync(executeAsync =>
            Vegha.Core.Flow.CollectionRunner.RunFolderAsync(folderVm.Folder, outerChain, executeAsync, default,
                onProgress: r => RunOnUi(() => LastRunResults.Add(r))));
    }

    private async Task ExecuteRunAsync(
        Func<Func<RequestItem, IReadOnlyList<Folder>, CancellationToken, Task<Vegha.Core.Flow.RequestRunResult>>,
            Task<IReadOnlyList<Vegha.Core.Flow.RequestRunResult>>> launch)
    {
        if (_httpExecutor is null)
        {
            StatusMessage = "HTTP executor not available — cannot run.";
            return;
        }

        IsRunning = true;
        LastRunResults.Clear();
        try
        {
            var executor = _httpExecutor;
            Task<Vegha.Core.Flow.RequestRunResult> ExecuteOne(
                RequestItem req, IReadOnlyList<Folder> chain, CancellationToken ct) =>
                ExecuteRequestAsync(executor, req, chain, ct);

            var results = await launch(ExecuteOne).ConfigureAwait(true);
            var passed = results.Count(r => r.Succeeded);
            StatusMessage = $"Run complete: {passed}/{results.Count} passed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collection run failed");
            StatusMessage = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async Task<Vegha.Core.Flow.RequestRunResult> ExecuteRequestAsync(
        Vegha.Core.Requests.HttpExecutor executor,
        RequestItem req,
        IReadOnlyList<Folder> chain,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return new Vegha.Core.Flow.RequestRunResult(req.Name, req.Method, req.Url, 0, 0, false, "URL not absolute");

        var headers = req.Headers.Where(h => h.Enabled)
            .Select(h => new KeyValuePair<string, string>(h.Name, h.Value))
            .ToList();

        var body = req.Body.Mode == BodyMode.None ? null : req.Body.Content;
        var contentType = req.Body.Mode switch
        {
            BodyMode.Json => "application/json",
            BodyMode.Xml => "application/xml",
            BodyMode.Text => "text/plain",
            _ => null,
        };

        try
        {
            var result = await executor.ExecuteAsync(
                new Vegha.Core.Requests.HttpExecutionRequest(
                    new HttpMethod(req.Method), uri, headers, body, contentType),
                ct).ConfigureAwait(false);
            var ok = result.StatusCode is >= 200 and < 400;
            return new Vegha.Core.Flow.RequestRunResult(
                req.Name, req.Method, req.Url,
                result.StatusCode, result.ElapsedMilliseconds, ok, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            return new Vegha.Core.Flow.RequestRunResult(req.Name, req.Method, req.Url, 0, 0, false, ex.Message);
        }
    }

    private (Collection? Collection, IReadOnlyList<Folder> OuterChain) ResolveFolderChain(CollectionFolderViewModel folderVm)
    {
        foreach (var root in Roots.OfType<CollectionRootViewModel>())
        {
            if (root.Collection is null) continue;
            var chain = new List<Folder>();
            if (FindFolderChain(root.Children, folderVm, chain))
                return (root.Collection, chain);
        }
        return (null, Array.Empty<Folder>());
    }

    private static bool FindFolderChain(
        IEnumerable<CollectionNodeViewModel> nodes,
        CollectionFolderViewModel target,
        List<Folder> chain)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (node is CollectionFolderViewModel folder)
            {
                if (folder.Folder is not null) chain.Add(folder.Folder);
                if (FindFolderChain(folder.Children, target, chain)) return true;
                if (folder.Folder is not null) chain.RemoveAt(chain.Count - 1);
            }
        }
        return false;
    }

    /// <summary>Reports a per-request result. The runner awaits between requests so the
    /// continuations already land on the calling thread; no extra marshalling needed.</summary>
    private static void RunOnUi(Action action) => action();

    [RelayCommand]
    private void GenerateCode(CollectionItemViewModel? item)
    {
        // Codegen is already accessible via the right-side panel; this command just selects the
        // request so the panel updates. The dedicated dialog (per the plan) lands in Phase 2.
        if (item is null) return;
        OpenRequest(item);
    }

    /// <summary>Holds a node path on the in-app clipboard for paste-into-folder. Cleared after
    /// each successful paste. Two cases: <see cref="CopyMode.Copy"/> deep-copies the source on
    /// paste; <see cref="CopyMode.Cut"/> moves it (which is what Bruno's Cut does).</summary>
    private (string Path, CopyMode Mode)? _clipboardNode;

    private enum CopyMode { Copy, Cut }

    [RelayCommand]
    private void CopyNode(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path)) { StatusMessage = "Nothing to copy."; return; }
        _clipboardNode = (path, CopyMode.Copy);
        StatusMessage = $"Copied “{node!.Name}”. Paste into a folder.";
    }

    [RelayCommand]
    private void PasteNode(CollectionNodeViewModel? target)
    {
        if (_clipboardNode is null) { StatusMessage = "Clipboard is empty."; return; }
        if (target is null) { StatusMessage = "Pick a folder to paste into."; return; }
        if (target is CollectionItemViewModel) { StatusMessage = "Paste target must be a folder."; return; }

        var (sourcePath, mode) = _clipboardNode.Value;
        var targetDir = ResolveNodeFilePath(target);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        { StatusMessage = "Could not resolve target folder."; return; }

        try
        {
            if (File.Exists(sourcePath))
            {
                var ext = System.IO.Path.GetExtension(sourcePath);
                var stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
                var name = NextUniqueName(targetDir, stem, ext);
                var dest = System.IO.Path.Combine(targetDir, name + ext);
                if (mode == CopyMode.Cut) File.Move(sourcePath, dest);
                else File.Copy(sourcePath, dest);
            }
            else if (Directory.Exists(sourcePath))
            {
                var leafName = System.IO.Path.GetFileName(sourcePath);
                var name = NextUniqueName(targetDir, leafName, string.Empty);
                var dest = System.IO.Path.Combine(targetDir, name);
                if (mode == CopyMode.Cut) Directory.Move(sourcePath, dest);
                else CopyDirectoryRecursive(sourcePath, dest);
            }
            else { StatusMessage = "Source no longer exists."; return; }

            _clipboardNode = null;
            ReloadParentRoot(target);
            StatusMessage = $"Pasted into “{target.Name}”.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Paste failed: {Source} → {Target}", sourcePath, targetDir);
            StatusMessage = $"Paste failed: {ex.Message}";
        }
    }

    /// <summary>Saves the current request's most recent response as <c>&lt;name&gt;.example.json</c>
    /// next to the request file. Picks up status, headers, and body from the active editor.</summary>
    [RelayCommand]
    private void CreateExample(CollectionItemViewModel? item)
    {
        if (item is null) { StatusMessage = "No request selected."; return; }
        var requestPath = item.SourcePath;
        if (string.IsNullOrEmpty(requestPath))
        { StatusMessage = "Request must be saved before creating an example."; return; }

        // Pull the live response from the editor — either the active tab's editor (when tabs
        // are wired) or the legacy single editor.
        var editor = _openTabs?.ActiveTab is Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel httpTab
            ? httpTab.Editor : _requestEditor;
        if (editor.ResponseStatusCode == 0)
        { StatusMessage = "Send the request first to capture a response."; return; }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(requestPath) ?? string.Empty;
            var stem = System.IO.Path.GetFileNameWithoutExtension(requestPath);
            var examplePath = System.IO.Path.Combine(dir, stem + ".example.json");
            var snapshot = new
            {
                statusCode = editor.ResponseStatusCode,
                statusText = editor.ResponseStatusText,
                headers = editor.ResponseHeaders.Select(h => new { name = h.Name, value = h.Value }),
                body = editor.ResponseBody,
                capturedAt = DateTime.UtcNow.ToString("o"),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(examplePath, json);
            StatusMessage = $"Saved example → {System.IO.Path.GetFileName(examplePath)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Create example failed");
            StatusMessage = $"Create example failed: {ex.Message}";
        }
    }

    /// <summary>Opens the collection-level settings dialog (collection vars, headers, auth, scripts).
    /// Dialog UI is lightweight for this cut — host opens it; the VM side just exposes the
    /// resolved Collection so the dialog can edit and CollectionStore can save.</summary>
    public Collection? GetCollectionForSettings(CollectionRootViewModel? root) =>
        root?.Collection;

    /// <summary>Same as <see cref="GetCollectionForSettings"/> but for folders. Returns the
    /// Folder + its containing root path so the host can save back to disk.</summary>
    public (Folder?, string?) GetFolderForSettings(CollectionFolderViewModel? folder) =>
        (folder?.Folder, folder is null ? null : FindRootOf(folder)?.SourcePath);

    /// <summary>Raised when the user picks Properties on a collection or folder. The host
    /// (CollectionsPanel) listens, opens the dialog, and on save calls
    /// <see cref="ApplyNodeSnapshot"/> to write back to disk + reload.</summary>
    public event EventHandler<NodePropertiesRequest>? NodePropertiesRequested;

    [RelayCommand]
    private void OpenCollectionSettings(CollectionRootViewModel? root)
    {
        if (root?.Collection is null) { StatusMessage = "No collection selected."; return; }
        NodePropertiesRequested?.Invoke(this,
            new NodePropertiesRequest(root, null));
    }

    [RelayCommand]
    private void OpenFolderSettings(CollectionFolderViewModel? folder)
    {
        if (folder?.Folder is null) { StatusMessage = "No folder selected."; return; }
        NodePropertiesRequested?.Invoke(this,
            new NodePropertiesRequest(null, folder));
    }

    /// <summary>Writes the edited collection or folder back to disk via BruMetaEmitter and
    /// reloads the affected root so the in-memory tree picks up the change. Called by the
    /// panel after the Properties dialog is dismissed with Save.</summary>
    public void ApplyNodeSnapshot(NodePropertiesRequest source, NodeSnapshot snapshot)
    {
        try
        {
            if (snapshot.Kind == NodePropertiesViewModel.Kind.Collection &&
                source.Root is { Collection: not null } root && snapshot.Collection is not null)
            {
                var path = System.IO.Path.Combine(root.SourcePath, "collection.bru");
                var text = Vegha.Core.Importers.BruMetaEmitter.EmitCollection(snapshot.Collection);
                File.WriteAllText(path, text);
                ReloadRootContaining(root);
                StatusMessage = $"Saved collection properties → {root.Name}.";
            }
            else if (snapshot.Kind == NodePropertiesViewModel.Kind.Folder &&
                source.Folder is not null && snapshot.Folder is not null)
            {
                var folderPath = source.Folder.Path;
                if (string.IsNullOrEmpty(folderPath))
                { StatusMessage = "Could not resolve folder path."; return; }
                Directory.CreateDirectory(folderPath);
                var path = System.IO.Path.Combine(folderPath, "folder.bru");
                var text = Vegha.Core.Importers.BruMetaEmitter.EmitFolder(snapshot.Folder);
                File.WriteAllText(path, text);
                ReloadRootContaining(source.Folder);
                StatusMessage = $"Saved folder properties → {source.Folder.Name}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Save node properties failed");
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowNodeInfo(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path) || node is null) return;
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                StatusMessage = $"{node.Name}: {info.Length} bytes · modified {info.LastWriteTime:yyyy-MM-dd HH:mm}";
            }
            else if (Directory.Exists(path))
            {
                var entries = Directory.EnumerateFileSystemEntries(path).Count();
                StatusMessage = $"{node.Name}: {entries} entries";
            }
        }
        catch { /* best-effort status */ }
    }

    // ============================== Drag-and-drop move ==============================

    /// <summary>Moves <paramref name="source"/> into <paramref name="target"/> on disk and refreshes
    /// the affected collection roots in the UI tree. Target may be a folder or a root; if a request
    /// leaf is dropped, the caller should pass the leaf's parent. Returns <c>true</c> when a move
    /// actually happened (returns <c>false</c> for self-drop, descendant-drop, same-parent, or any
    /// IO failure — the user sees a status message either way).</summary>
    public bool MoveNode(CollectionNodeViewModel? source, CollectionNodeViewModel? target)
    {
        if (source is null || target is null) return false;
        if (source is CollectionRootViewModel) { StatusMessage = "Can't move a collection root."; return false; }
        if (ReferenceEquals(source, target)) return false;
        if (target is CollectionItemViewModel) { StatusMessage = "Drop target must be a folder."; return false; }

        // Disallow dropping a folder into itself or any of its descendants.
        if (source is CollectionFolderViewModel sf && IsDescendantOf(target, sf))
        { StatusMessage = "Can't move a folder into itself."; return false; }

        var sourcePath = ResolveNodeFilePath(source);
        var targetDir = ResolveNodeFilePath(target);
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetDir))
        { StatusMessage = "Could not resolve source or target path."; return false; }

        // Same-parent no-op: file/dir is already directly under the target dir.
        var sourceParentDir = global::System.IO.Path.GetDirectoryName(sourcePath);
        if (string.Equals(sourceParentDir, targetDir, StringComparison.OrdinalIgnoreCase))
        { StatusMessage = "Already in this folder."; return false; }

        try
        {
            if (source is CollectionItemViewModel item)
            {
                MoveRequestFiles(sourcePath, targetDir, item.Name);
            }
            else if (source is CollectionFolderViewModel folder)
            {
                var dest = global::System.IO.Path.Combine(targetDir, folder.Name);
                if (Directory.Exists(dest))
                { StatusMessage = $"A folder named '{folder.Name}' already exists in the target."; return false; }
                Directory.Move(sourcePath, dest);
            }

            // Reload affected roots — both source and target sides, in case they differ.
            ReloadRootContaining(source);
            if (!ReferenceEquals(FindRootOf(source), FindRootOf(target)))
                ReloadRootContaining(target);

            StatusMessage = $"Moved '{source.Name}' into '{target.Name}'.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Move failed: {Source} → {Target}", sourcePath, targetDir);
            StatusMessage = $"Move failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>A request lives as either <c>name.bru</c> (Bruno-style) or <c>name.req.json</c>
    /// (JSON format). Move whichever exists. The companion <c>.example.json</c> snapshot, if
    /// present, follows the .bru/.req.json file.</summary>
    private static void MoveRequestFiles(string sourcePath, string targetDir, string requestName)
    {
        Directory.CreateDirectory(targetDir);
        var fileName = global::System.IO.Path.GetFileName(sourcePath);
        var dest = global::System.IO.Path.Combine(targetDir, fileName);
        if (File.Exists(dest))
            throw new IOException($"A file named '{fileName}' already exists in the target folder.");
        File.Move(sourcePath, dest);

        // Pull along an adjacent example snapshot if the convention is present.
        var srcDir = global::System.IO.Path.GetDirectoryName(sourcePath);
        if (srcDir is null) return;
        var example = global::System.IO.Path.Combine(srcDir, requestName + ".example.json");
        if (File.Exists(example))
        {
            var exampleDest = global::System.IO.Path.Combine(targetDir, requestName + ".example.json");
            if (!File.Exists(exampleDest)) File.Move(example, exampleDest);
        }
    }

    private void ReloadRootContaining(CollectionNodeViewModel node)
    {
        var root = FindRootOf(node);
        if (root is null) return;
        var rootPath = root.SourcePath;
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return;

        var index = Roots.IndexOf(root);
        if (index < 0) return;

        // Snapshot which folder paths the user has expanded so the reload doesn't fully
        // collapse the tree. The Roots[index] = refreshed swap creates a fresh subtree
        // with IsExpanded=false everywhere; we re-apply the saved set after.
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedPaths(root, expanded);

        try
        {
            var collection = CollectionLoader.Load(rootPath);
            var refreshed = CollectionNodeViewModel.FromCollection(collection, rootPath);
            Roots[index] = refreshed;
            HookExpansionEvents(refreshed);

            _suppressExpansionEvents = true;
            try { ApplyExpandedPathsTo(refreshed, expanded); }
            finally { _suppressExpansionEvents = false; }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reload after move failed for {Path}", rootPath);
        }
    }

    /// <summary>Walks the subtree and adds the paths of every expanded folder/root into
    /// <paramref name="sink"/>. Leaf nodes don't carry expansion state.</summary>
    private static void CollectExpandedPaths(CollectionNodeViewModel node, HashSet<string> sink)
    {
        if (!node.IsLeaf && node.IsExpanded && !string.IsNullOrEmpty(node.Path))
            sink.Add(node.Path);
        foreach (var c in node.Children) CollectExpandedPaths(c, sink);
    }

    /// <summary>Sets IsExpanded=true on every folder/root whose path appears in
    /// <paramref name="set"/>. Reverse of <see cref="CollectExpandedPaths"/>.</summary>
    private static void ApplyExpandedPathsTo(CollectionNodeViewModel node, HashSet<string> set)
    {
        if (!node.IsLeaf) node.IsExpanded = set.Contains(node.Path);
        foreach (var c in node.Children) ApplyExpandedPathsTo(c, set);
    }

    private CollectionRootViewModel? FindRootOf(CollectionNodeViewModel node)
    {
        foreach (var root in Roots.OfType<CollectionRootViewModel>())
            if (ReferenceEquals(root, node) || ContainsNode(root.Children, node))
                return root;
        return null;
    }

    private static bool ContainsNode(IEnumerable<CollectionNodeViewModel> haystack, CollectionNodeViewModel needle)
    {
        foreach (var n in haystack)
        {
            if (ReferenceEquals(n, needle)) return true;
            if (ContainsNode(n.Children, needle)) return true;
        }
        return false;
    }

    private static bool IsDescendantOf(CollectionNodeViewModel candidate, CollectionFolderViewModel ancestor)
    {
        if (ReferenceEquals(candidate, ancestor)) return true;
        return ContainsNode(ancestor.Children, candidate);
    }

    // ============================== Helpers for context-menu commands ==============================

    /// <summary>Resolves the on-disk path of the node — file for requests, directory for folders/roots.</summary>
    private static string? ResolveNodeFilePath(CollectionNodeViewModel? node) => node switch
    {
        CollectionItemViewModel item => item.SourcePath ?? (string.IsNullOrEmpty(item.Path) ? null : item.Path),
        CollectionFolderViewModel folder => string.IsNullOrEmpty(folder.Path) ? null : folder.Path,
        CollectionRootViewModel root => string.IsNullOrEmpty(root.SourcePath) ? null : root.SourcePath,
        _ => null,
    };

    /// <summary>For folder/root nodes, the directory itself; for request nodes, their parent dir.</summary>
    private static string? ResolveNodeDirectoryPath(CollectionNodeViewModel? node)
    {
        var path = ResolveNodeFilePath(node);
        if (string.IsNullOrEmpty(path)) return null;
        if (Directory.Exists(path)) return path;
        return System.IO.Path.GetDirectoryName(path);
    }

    private void RemoveNodeFromParent(CollectionNodeViewModel target)
    {
        foreach (var root in Roots)
        {
            if (root == target) { Roots.Remove(root); return; }
            if (RemoveFromChildren(root, target)) return;
        }
    }

    private static bool RemoveFromChildren(CollectionNodeViewModel parent, CollectionNodeViewModel target)
    {
        if (parent.Children.Remove(target)) return true;
        foreach (var c in parent.Children)
        {
            if (RemoveFromChildren(c, target)) return true;
        }
        return false;
    }

    /// <summary>Reloads the root containing the node so on-disk changes show in the tree.
    /// Delegates to <see cref="ReloadRootContaining"/> which replaces the root in-place
    /// (LoadFromDirectory would append a second root each time, which is the bug that
    /// previously made create/clone/rename invisible until the app was restarted).</summary>
    private void ReloadParentRoot(CollectionNodeViewModel? node)
    {
        if (node is null) return;
        ReloadRootContaining(node);
    }

    private CollectionRootViewModel? FindRootFor(CollectionNodeViewModel node)
    {
        // Walk Roots; if any contains the node anywhere in its tree, that's the owning root.
        foreach (var r in Roots)
        {
            if (r == node && r is CollectionRootViewModel cr) return cr;
            if (r is CollectionRootViewModel cr2 && Contains(cr2, node)) return cr2;
        }
        return null;
    }

    private static bool Contains(CollectionNodeViewModel root, CollectionNodeViewModel needle)
    {
        if (root == needle) return true;
        foreach (var c in root.Children)
            if (Contains(c, needle)) return true;
        return false;
    }

    /// <summary>Minimal cURL command parser. Handles the common shapes — <c>-X METHOD</c>,
    /// <c>-H 'Name: Value'</c>, <c>-d</c> / <c>--data</c> / <c>--data-raw</c> / <c>--data-binary</c>,
    /// and the URL (positional or after <c>--url</c>). Sufficient for most pasted curl commands
    /// from browser DevTools / Postman / API docs. Returns null when the command is unparseable
    /// (e.g. has no URL).</summary>
    private static RequestItem? ParseCurlCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var args = TokenizeShellLike(command);
        if (args.Count == 0) return null;

        // Drop a leading "curl" if present.
        var i = 0;
        if (string.Equals(args[i], "curl", StringComparison.OrdinalIgnoreCase)) i++;

        string? method = null;
        string? url = null;
        var headers = new List<KvPair>();
        string? body = null;
        var contentType = (string?)null;

        while (i < args.Count)
        {
            var a = args[i];
            switch (a)
            {
                case "-X":
                case "--request":
                    if (i + 1 < args.Count) { method = args[++i].ToUpperInvariant(); }
                    break;
                case "-H":
                case "--header":
                    if (i + 1 < args.Count)
                    {
                        var raw = args[++i];
                        var colon = raw.IndexOf(':');
                        if (colon > 0)
                        {
                            var hname = raw[..colon].Trim();
                            var hvalue = raw[(colon + 1)..].Trim();
                            headers.Add(new KvPair(hname, hvalue));
                            if (string.Equals(hname, "Content-Type", StringComparison.OrdinalIgnoreCase))
                                contentType = hvalue;
                        }
                    }
                    break;
                case "-d":
                case "--data":
                case "--data-raw":
                case "--data-binary":
                case "--data-urlencode":
                    if (i + 1 < args.Count) body = args[++i];
                    method ??= "POST";
                    break;
                case "-u":
                case "--user":
                    if (i + 1 < args.Count)
                    {
                        // Embed as a Basic header for simplicity — a fuller importer would
                        // populate AuthConfig instead.
                        var creds = args[++i];
                        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(creds));
                        headers.Add(new KvPair("Authorization", "Basic " + b64));
                    }
                    break;
                case "--url":
                    if (i + 1 < args.Count) url = args[++i];
                    break;
                case "-A":
                case "--user-agent":
                    if (i + 1 < args.Count) headers.Add(new KvPair("User-Agent", args[++i]));
                    break;
                case "-e":
                case "--referer":
                    if (i + 1 < args.Count) headers.Add(new KvPair("Referer", args[++i]));
                    break;
                case "-I":
                case "--head":
                    method ??= "HEAD";
                    break;
                case "-G":
                case "--get":
                    method ??= "GET";
                    break;
                default:
                    // First non-option that looks like a URL becomes the target URL.
                    if (url is null && (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                        || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                        || a.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                                        || a.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
                        url = a;
                    // Quietly skip unknown flags (e.g. -k, --compressed, -L, -i, -s) so the
                    // request still imports cleanly.
                    break;
            }
            i++;
        }

        if (string.IsNullOrEmpty(url)) return null;
        method ??= "GET";

        var bodyConfig = new BodyConfig();
        if (!string.IsNullOrEmpty(body))
        {
            var ct = contentType ?? string.Empty;
            var mode = ct.Contains("json", StringComparison.OrdinalIgnoreCase) ? BodyMode.Json
                : ct.Contains("xml", StringComparison.OrdinalIgnoreCase) ? BodyMode.Xml
                : ct.Contains("urlencoded", StringComparison.OrdinalIgnoreCase) ? BodyMode.FormUrlEncoded
                : BodyMode.Text;
            bodyConfig = new BodyConfig { Mode = mode, Content = body };
        }

        var name = "from-curl";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var seg = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrEmpty(seg)) name = seg;
        }

        return new RequestItem
        {
            Name = name,
            Method = method,
            Url = url,
            Headers = headers,
            Body = bodyConfig,
        };
    }

    /// <summary>Splits a command line honoring single + double quotes, line-continuation
    /// backslashes, and basic escape sequences. Doesn't try to be a full POSIX shell, but
    /// covers the way users actually paste curl commands.</summary>
    private static List<string> TokenizeShellLike(string input)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        var inToken = false;

        for (var idx = 0; idx < input.Length; idx++)
        {
            var c = input[idx];

            if (quote is null && c == '\\' && idx + 1 < input.Length)
            {
                var next = input[idx + 1];
                // Line-continuation: skip the following newline so a multiline curl pastes cleanly.
                if (next == '\n' || next == '\r') { idx++; continue; }
                sb.Append(next); idx++; inToken = true; continue;
            }
            if (quote is { } q && c == '\\' && idx + 1 < input.Length && q == '"')
            {
                sb.Append(input[idx + 1]); idx++; continue;
            }
            if (quote is null && (c == '\'' || c == '"')) { quote = c; inToken = true; continue; }
            if (quote == c) { quote = null; continue; }
            if (quote is null && char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(sb.ToString()); sb.Clear(); inToken = false; }
                continue;
            }
            sb.Append(c); inToken = true;
        }
        if (inToken) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Rewrites the <c>name:</c> entry inside a .bru file's <c>meta { ... }</c> block
    /// so a cloned request shows the new file name rather than the source's. Operates on the
    /// raw text — cheap regex on a single line; preserves the rest of the file byte-for-byte.</summary>
    private static void TryRewriteBruMetaName(string bruPath, string newName)
    {
        try
        {
            var text = File.ReadAllText(bruPath);
            var pattern = new System.Text.RegularExpressions.Regex(
                @"(?m)^(\s*name:\s*).*$");
            var replaced = pattern.Replace(text, $"$1{newName}", count: 1);
            if (!ReferenceEquals(replaced, text))
                File.WriteAllText(bruPath, replaced);
        }
        catch { /* best-effort */ }
    }

    private static string NextUniqueName(string dir, string baseName, string extension)
    {
        var candidate = baseName;
        var i = 2;
        while (File.Exists(System.IO.Path.Combine(dir, candidate + extension))
            || Directory.Exists(System.IO.Path.Combine(dir, candidate)))
        {
            candidate = baseName + " " + i++;
            if (i > 1000) break;
        }
        return candidate;
    }

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>Walks every node in every root, marking <c>IsVisibleByFilter</c>:
    /// <list type="bullet">
    ///   <item>Leaves: match if their <see cref="CollectionNodeViewModel.Name"/> contains the
    ///   filter (case-insensitive). Empty filter → always visible.</item>
    ///   <item>Folders / roots: visible if their own name matches OR any descendant matches.
    ///   Folders that contain a match auto-expand so the user can see the result.</item>
    /// </list></summary>
    private void ApplyFilter()
    {
        var needle = (Filter ?? string.Empty).Trim();
        var empty = string.IsNullOrEmpty(needle);
        foreach (var root in Roots) WalkApply(root, needle, empty);

        bool WalkApply(CollectionNodeViewModel node, string n, bool emptyFilter)
        {
            bool selfMatch = emptyFilter || node.Name.Contains(n, StringComparison.OrdinalIgnoreCase);
            bool anyChild = false;
            foreach (var c in node.Children)
                if (WalkApply(c, n, emptyFilter)) anyChild = true;

            node.IsVisibleByFilter = selfMatch || anyChild;
            // Auto-expand folders that contain a match while the filter is active so the
            // matching leaves are immediately visible. Don't collapse anything — the user
            // may have manually expanded folders that don't match but want to keep open.
            if (!emptyFilter && anyChild && !node.IsLeaf) node.IsExpanded = true;
            return node.IsVisibleByFilter;
        }
    }

    /// <summary>Rewrites <c>meta.name</c> inside <c>folder.bru</c> after a directory rename.
    /// Bruno's loader prefers the meta name over the directory name, so without this the
    /// renamed folder would keep displaying its old label. If <c>folder.bru</c> is missing
    /// the directory name is authoritative — leave it alone. If the <c>meta {}</c> block
    /// has no <c>name:</c> entry yet, insert one.</summary>
    private static void UpdateFolderBruMetaName(string folderDir, string newName)
    {
        var bru = System.IO.Path.Combine(folderDir, "folder.bru");
        if (!File.Exists(bru)) return;
        try
        {
            var text = File.ReadAllText(bru);
            var lines = text.Split('\n').ToList();
            // Walk to the meta { block, then look for an existing name: line inside it.
            int metaStart = -1, metaEnd = -1, nameLineIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (metaStart < 0)
                {
                    if (trimmed.StartsWith("meta", StringComparison.Ordinal) && trimmed.Contains('{'))
                        metaStart = i;
                    continue;
                }
                if (trimmed.StartsWith("}", StringComparison.Ordinal)) { metaEnd = i; break; }
                if (trimmed.StartsWith("name:", StringComparison.Ordinal)) nameLineIndex = i;
            }

            if (metaStart < 0)
            {
                // No meta block at all — prepend one. Preserves the rest of the file.
                var meta = $"meta {{\n  name: {newName}\n}}\n\n";
                File.WriteAllText(bru, meta + text);
                return;
            }

            if (nameLineIndex >= 0)
            {
                var indent = lines[nameLineIndex].Length - lines[nameLineIndex].TrimStart().Length;
                lines[nameLineIndex] = new string(' ', Math.Max(2, indent)) + "name: " + newName;
            }
            else
            {
                // meta { } exists but no name field — insert before the closing brace.
                if (metaEnd < 0) return;
                lines.Insert(metaEnd, "  name: " + newName);
            }

            File.WriteAllText(bru, string.Join('\n', lines));
        }
        catch
        {
            // Best-effort: a malformed folder.bru shouldn't fail the rename — the directory
            // move has already happened and is the operation the user actually cares about.
        }
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = System.IO.Path.GetFileName(file);
            File.Copy(file, System.IO.Path.Combine(dest, name));
        }
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            var name = System.IO.Path.GetFileName(sub);
            CopyDirectoryRecursive(sub, System.IO.Path.Combine(dest, name));
        }
    }

    private static int CountRequests(Collection c)
    {
        int total = c.Requests.Count;
        foreach (var f in c.Folders) total += CountRequestsInFolder(f);
        return total;
    }

    private static int CountRequestsInFolder(Folder f)
    {
        int total = f.Requests.Count;
        foreach (var sub in f.Folders) total += CountRequestsInFolder(sub);
        return total;
    }
}

/// <summary>Tree node representing either a Collection root, a Folder, or a Request.</summary>
public abstract partial class CollectionNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>True when this node passes the active filter, including the case where any
    /// descendant matches. Bound via a TreeViewItem style so non-matching rows fold up.</summary>
    [ObservableProperty]
    private bool _isVisibleByFilter = true;

    /// <summary>Stable identifier used to persist expansion state across sessions.
    /// Roots use their absolute folder path; folders are <c>parent/name</c>.</summary>
    public string Path { get; init; } = string.Empty;

    public ObservableCollection<CollectionNodeViewModel> Children { get; init; } = new();

    public abstract bool IsLeaf { get; }

    /// <summary>Fires when this non-leaf node toggles expansion. Leaves never raise.</summary>
    public event EventHandler<bool>? ExpansionChanged;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!IsLeaf) ExpansionChanged?.Invoke(this, value);
    }

    public static CollectionNodeViewModel FromCollection(Collection col, string rootPath) =>
        new CollectionRootViewModel
        {
            Name = col.Name,
            Path = rootPath,
            SourcePath = rootPath,
            Collection = col,
            Children = ToChildren(col.Folders, col.Requests, rootPath),
        };

    private static ObservableCollection<CollectionNodeViewModel> ToChildren(
        IEnumerable<Folder> folders, IEnumerable<RequestItem> requests, string parentPath)
    {
        var children = new ObservableCollection<CollectionNodeViewModel>();
        foreach (var f in folders)
        {
            var folderPath = global::System.IO.Path.Combine(parentPath, f.Name);
            children.Add(new CollectionFolderViewModel
            {
                Name = f.Name,
                Path = folderPath,
                Folder = f,
                Children = ToChildren(f.Folders, f.Requests, folderPath),
            });
        }
        foreach (var r in requests)
        {
            var bruFile = FindBruFile(parentPath, r.Name);
            children.Add(new CollectionItemViewModel
            {
                Name = r.Name,
                Path = bruFile ?? global::System.IO.Path.Combine(parentPath, r.Name + ".bru"),
                Request = r,
                MethodLabel = r.Method,
                SourcePath = bruFile,
            });
        }
        return children;
    }

    private static string? FindBruFile(string folderPath, string requestName)
    {
        if (!Directory.Exists(folderPath)) return null;
        // Try exact name match first.
        var candidate = global::System.IO.Path.Combine(folderPath, requestName + ".bru");
        if (File.Exists(candidate)) return candidate;
        // Otherwise scan for a file whose meta.name matches.
        foreach (var file in Directory.EnumerateFiles(folderPath, "*.bru", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = BruParser.Parse(File.ReadAllText(file));
                var meta = doc.Blocks.OfType<DictBlock>().FirstOrDefault(b => b.Name == "meta");
                var name = (meta?.Pairs.FirstOrDefault(p => p.Name == "name")?.Value as StringValue)?.Text;
                if (name == requestName) return file;
            }
            catch { /* skip */ }
        }
        return null;
    }
}

public sealed partial class CollectionRootViewModel : CollectionNodeViewModel
{
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>The Domain.Collection this root was built from. Lets RequestComposition
    /// reach the collection-level Headers / Auth / Vars / Scripts at execution time.</summary>
    public Collection? Collection { get; init; }

    public override bool IsLeaf => false;
}

public sealed partial class CollectionFolderViewModel : CollectionNodeViewModel
{
    /// <summary>The Domain.Folder this VM mirrors. Lets RequestComposition reach the
    /// folder-level inheritance fields at execution time.</summary>
    public Folder? Folder { get; init; }

    public override bool IsLeaf => false;
}

public sealed partial class CollectionItemViewModel : CollectionNodeViewModel
{
    public RequestItem? Request { get; init; }
    public string? SourcePath { get; init; }

    [ObservableProperty]
    private string _methodLabel = "GET";

    public override bool IsLeaf => true;
}

public enum NodePropertiesKind { Collection, Folder }

/// <summary>Carries the source node references the host needs after the Properties dialog
/// closes — to know which on-disk path to write to + which root to reload.</summary>
public sealed record NodePropertiesRequest(
    CollectionRootViewModel? Root,
    CollectionFolderViewModel? Folder);

/// <summary>Kind of request the user picked in the New Request dialog. Lives in the
/// ViewModels project so <see cref="CollectionsViewModel.CreateRequestFromDialog"/> can
/// emit the right meta.type without taking a dependency on the Controls layer.</summary>
public enum NewRequestKind { Http, GraphQL, WebSocket, Grpc, Soap, FromCurl }

/// <summary>Payload for <see cref="CollectionsViewModel.ActiveCollectionChanged"/>.</summary>
public sealed record ActiveCollectionChangedEventArgs(string? OldCollectionPath, string? NewCollectionPath);
