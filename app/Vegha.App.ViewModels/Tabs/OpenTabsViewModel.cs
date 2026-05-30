using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Manages the set of open request tabs. The collections sidebar opens or activates
/// tabs through this VM; the tab strip in the chrome binds to <see cref="Tabs"/>
/// + <see cref="ActiveTab"/>; closing tabs flips <see cref="ActiveTab"/> to a sensible
/// neighbor.
///
/// Tab identity is the request's source path on disk (so the same request opened
/// twice activates the existing tab instead of creating a duplicate). Drafts not yet
/// saved get a "draft:GUID" id.
/// </summary>
public partial class OpenTabsViewModel : ObservableObject
{
    private readonly Func<RequestEditorViewModel> _editorFactory;
    private readonly Func<SoapWorkspaceViewModel>? _soapFactory;
    private readonly ILogger<OpenTabsViewModel> _logger;

    public ObservableCollection<RequestTabViewModel> Tabs { get; } = new();

    /// <summary>Filtered view of <see cref="Tabs"/> showing only tabs that match the current
    /// <see cref="ActiveScope"/> (active collection path). Untagged tabs (legacy / scope-less
    /// drafts) appear in every scope so they never vanish entirely. The tab strip binds here.</summary>
    public ObservableCollection<RequestTabViewModel> VisibleTabs { get; } = new();

    /// <summary>Absolute collection-root path currently scoping the tab strip. Setting this
    /// rebuilds <see cref="VisibleTabs"/> and restores the per-scope last-active tab.</summary>
    [ObservableProperty]
    private string? _activeScope;

    /// <summary>Active workspace (its folder path). Scratch tabs are tagged with the workspace
    /// they were created in and only appear while that workspace is active — so quick "+" drafts
    /// never leak across workspaces. Set by the host on startup and every workspace switch.</summary>
    [ObservableProperty]
    private string? _activeWorkspaceId;

    /// <summary>Which sidebar mode is active — drives a *kind* filter on the tab strip so
    /// diff tabs only appear in Source Control mode and request tabs only appear in the
    /// non-git modes. Set externally by <c>MainWindowViewModel</c> on every sidebar switch.</summary>
    [ObservableProperty]
    private bool _isGitMode;

    /// <summary>True while the History sidebar section is active. Mirrors <see cref="IsGitMode"/>
    /// but for <see cref="HistoryTabViewModel"/> — in this mode only history tabs are visible.</summary>
    [ObservableProperty]
    private bool _isHistoryMode;

    /// <summary>True while the Runner sidebar section is active. Filters the tab strip to
    /// <see cref="CollectionRunTabViewModel"/> entries so run tabs don't crowd the editor's
    /// tab strip in non-runner modes.</summary>
    [ObservableProperty]
    private bool _isRunnerMode;

    /// <summary>Per-scope memory of which tab was active when the user left that scope. Used to
    /// restore focus on collection switch so the user returns to the request they were editing.</summary>
    private readonly Dictionary<string, string> _lastActiveByScope =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-workspace memory of the last-active tab. Collection tabs are restored by
    /// <see cref="_lastActiveByScope"/> on collection switch, but scratch tabs (no collection)
    /// need their own per-workspace memory so returning to a workspace re-selects the draft the
    /// user was on rather than snapping to the first one.</summary>
    private readonly Dictionary<string, string> _lastActiveByWorkspace =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseActiveTabCommand))]
    [NotifyPropertyChangedFor(nameof(IsResponsePaneApplicable))]
    private RequestTabViewModel? _activeTab;

    /// <summary>True when the active tab is an HTTP/GraphQL request (i.e. the one that has
    /// a response). Workspace + environment editor tabs return false so MainWindow can hide
    /// the response splitter + response display and let the editor have the full area.</summary>
    public bool IsResponsePaneApplicable => ActiveTab is HttpRequestTabViewModel;

    partial void OnActiveTabChanged(RequestTabViewModel? oldValue, RequestTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;

        // Track per-scope last-active continuously so scope switches can restore focus
        // accurately, even when the user has opened tabs in another scope in the meantime.
        if (newValue is not null && !string.IsNullOrEmpty(newValue.CollectionPath))
            _lastActiveByScope[newValue.CollectionPath] = newValue.Id;

        // Track per-workspace last-active too (covers scratch tabs, which have no collection)
        // so a workspace round-trip returns to the same tab.
        if (newValue is not null && !string.IsNullOrEmpty(ActiveWorkspaceId))
            _lastActiveByWorkspace[ActiveWorkspaceId] = newValue.Id;
    }

    /// <summary>Fires when <see cref="Tabs"/> changes (open/close/reorder) so the host can persist.</summary>
    public event EventHandler? TabsChanged;

    public OpenTabsViewModel(Func<RequestEditorViewModel> editorFactory, ILogger<OpenTabsViewModel> logger)
        : this(editorFactory, soapFactory: null, logger)
    {
    }

    public OpenTabsViewModel(
        Func<RequestEditorViewModel> editorFactory,
        Func<SoapWorkspaceViewModel>? soapFactory,
        ILogger<OpenTabsViewModel> logger)
    {
        _editorFactory = editorFactory;
        _soapFactory = soapFactory;
        _logger = logger;
        Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    private void OnTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        TabsChanged?.Invoke(this, EventArgs.Empty);

        // Mirror added/removed entries into VisibleTabs when they match the active scope. We
        // keep a manual mirror instead of an ICollectionView so the existing
        // ObservableCollection bindings on the tab strip work unchanged.
        if (e.NewItems is not null)
            foreach (var t in e.NewItems.OfType<RequestTabViewModel>())
                if (MatchesScope(t)) VisibleTabs.Add(t);
        if (e.OldItems is not null)
            foreach (var t in e.OldItems.OfType<RequestTabViewModel>())
                VisibleTabs.Remove(t);
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            RebuildVisibleTabs();
    }

    private bool MatchesScope(RequestTabViewModel tab)
    {
        // Kind filter first: each "mode" claims a single tab kind and hides the others.
        //   - Git mode shows only diff tabs.
        //   - History mode shows only history tabs.
        //   - Runner mode shows only collection-run tabs.
        //   - Default mode shows request/workspace/env tabs (hides diff/history/run).
        // HistoryTabViewModel inherits HttpRequestTabViewModel, so the history check must
        // come BEFORE any IsAssignableFrom-style check on the HTTP base.
        var isDiff = tab is GitDiffTabViewModel;
        var isHistory = tab is HistoryTabViewModel;
        var isRun = tab is CollectionRunTabViewModel;
        if (IsGitMode) return isDiff;
        if (IsHistoryMode) return isHistory;
        if (IsRunnerMode) return isRun;
        if (isDiff || isHistory || isRun) return false;

        // Scratch ("+") tabs belong to a workspace, not a collection — they float across that
        // workspace's collections but must NOT appear under any other workspace.
        if (tab.IsScratch)
            return string.Equals(tab.WorkspaceId, ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase);

        // Untagged tabs (legacy persisted entries, scope-less drafts) float across every
        // collection scope — they have no collection to belong to. Tagged tabs are visible
        // only when their CollectionPath matches the active scope; if no scope is active
        // (e.g., the user switched to a workspace that has no collections yet), tagged tabs
        // from the *previous* scope must NOT leak through.
        if (tab.CollectionPath is null) return true;
        if (string.IsNullOrEmpty(ActiveScope)) return false;
        return string.Equals(tab.CollectionPath, ActiveScope, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsGitModeChanged(bool oldValue, bool newValue) => RefreshVisibleAndActiveTab();
    partial void OnIsHistoryModeChanged(bool oldValue, bool newValue) => RefreshVisibleAndActiveTab();
    partial void OnIsRunnerModeChanged(bool oldValue, bool newValue) => RefreshVisibleAndActiveTab();

    partial void OnActiveWorkspaceIdChanged(string? oldValue, string? newValue)
    {
        // Remember the active tab for the workspace we're leaving so a round-trip restores it.
        if (!string.IsNullOrEmpty(oldValue) && ActiveTab is { } at)
            _lastActiveByWorkspace[oldValue] = at.Id;

        RebuildVisibleTabs();

        // Keep the current active tab if it's still visible (e.g. a collection tab whose scope
        // hasn't been switched yet); otherwise restore the workspace's remembered active —
        // crucial for scratch tabs, which OnActiveScopeChanged can't restore. Falls back to the
        // first visible tab.
        if (ActiveTab is not null && VisibleTabs.Contains(ActiveTab)) return;

        RequestTabViewModel? next = null;
        if (!string.IsNullOrEmpty(newValue)
            && _lastActiveByWorkspace.TryGetValue(newValue, out var rememberedId))
        {
            next = VisibleTabs.FirstOrDefault(t => string.Equals(t.Id, rememberedId, StringComparison.OrdinalIgnoreCase));
        }
        ActiveTab = next ?? VisibleTabs.FirstOrDefault();
    }

    private void RefreshVisibleAndActiveTab()
    {
        RebuildVisibleTabs();
        // Re-pick an active tab from the now-visible subset.
        if (ActiveTab is null || !VisibleTabs.Contains(ActiveTab))
            ActiveTab = VisibleTabs.FirstOrDefault();
    }

    private void RebuildVisibleTabs()
    {
        VisibleTabs.Clear();
        foreach (var t in Tabs)
            if (MatchesScope(t)) VisibleTabs.Add(t);
    }

    partial void OnActiveScopeChanged(string? oldValue, string? newValue)
    {
        // Remember which tab the user had focused INSIDE the leaving scope so we can restore
        // it on return. Only store when the current ActiveTab actually belongs to that scope
        // (an untagged or cross-scope ActiveTab shouldn't pollute /A's remembered selection).
        if (!string.IsNullOrEmpty(oldValue)
            && ActiveTab is { } at
            && string.Equals(at.CollectionPath, oldValue, StringComparison.OrdinalIgnoreCase))
        {
            _lastActiveByScope[oldValue] = at.Id;
        }

        RebuildVisibleTabs();

        // If the current ActiveTab is still visible in the new scope, leave it alone — the
        // user's selection has not changed. Otherwise restore from per-scope memory, falling
        // back to the first visible tab (or null if the scope has none).
        if (ActiveTab is not null && VisibleTabs.Contains(ActiveTab)) return;

        RequestTabViewModel? next = null;
        if (!string.IsNullOrEmpty(newValue)
            && _lastActiveByScope.TryGetValue(newValue, out var rememberedId))
        {
            next = VisibleTabs.FirstOrDefault(t => string.Equals(t.Id, rememberedId, StringComparison.OrdinalIgnoreCase));
        }
        ActiveTab = next ?? VisibleTabs.FirstOrDefault();
    }

    /// <summary>External env-snapshot supplier — set by CollectionsViewModel after construction
    /// so newly-built tabs (drafts, restored tabs, dialog-driven creates) can immediately see
    /// the active environment's <c>{{var}}</c> values without waiting for an env switch event.
    /// Returns null when no env is active.</summary>
    public Func<IReadOnlyDictionary<string, string>?>? EnvironmentSnapshotProvider { get; set; }

    /// <summary>External supplier for the active env's secret variable names — paired with
    /// <see cref="EnvironmentSnapshotProvider"/> so newly-built tabs can redact resolved
    /// secret values from copyable surfaces. Returns null when no env is active.</summary>
    public Func<IReadOnlyCollection<string>?>? SecretNamesProvider { get; set; }

    /// <summary>External workspace-context supplier. Used by newly-built tabs to merge
    /// workspace-level vars + scripts into the compose chain. Returns
    /// <see cref="Vegha.Core.Requests.RequestComposition.WorkspaceContext.Empty"/> when no
    /// workspace context applies.</summary>
    public Func<Vegha.Core.Requests.RequestComposition.WorkspaceContext>? WorkspaceContextProvider { get; set; }

    /// <summary>Opens the request in a new tab, or activates the existing tab if it's already open.
    /// Optional <paramref name="collection"/> + <paramref name="folderChain"/> let the editor
    /// resolve inheritance (Collection → Folder → Request) at execution time. The
    /// <paramref name="collectionPath"/> stamps the tab so it can be filtered by the active
    /// collection in the tab strip.</summary>
    public RequestTabViewModel OpenOrActivate(
        RequestItem request,
        string? sourcePath,
        Vegha.Core.Domain.Collection? collection = null,
        IReadOnlyList<Vegha.Core.Domain.Folder>? folderChain = null,
        string? collectionPath = null)
    {
        var id = sourcePath ?? "request:" + request.Name + ":" + Guid.NewGuid().ToString("N");
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // Refresh parent context on re-open in case the user reorganized the tree.
            if (existing is HttpRequestTabViewModel http) http.SetParentContext(collection, folderChain);
            ActiveTab = existing;
            return existing;
        }

        RequestTabViewModel tab = request.Kind == RequestKind.Soap && _soapFactory is not null
            ? new SoapRequestTabViewModel(_soapFactory(), request, sourcePath, id) { CollectionPath = collectionPath }
            : BuildHttpTab(request, sourcePath, id, collection, folderChain, collectionPath);

        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    private HttpRequestTabViewModel BuildHttpTab(
        RequestItem request, string? sourcePath, string id,
        Vegha.Core.Domain.Collection? collection,
        IReadOnlyList<Vegha.Core.Domain.Folder>? folderChain,
        string? collectionPath)
    {
        var editor = _editorFactory();
        // Wire the workspace context BEFORE LoadFromRequestItem so the initial inheritance
        // hints (refreshed during SetParentContext below) see the workspace layer.
        if (WorkspaceContextProvider is not null) editor.WorkspaceContextProvider = WorkspaceContextProvider;
        editor.LoadFromRequestItem(request, sourcePath);
        var snapshot = EnvironmentSnapshotProvider?.Invoke();
        if (snapshot is not null) editor.EnvironmentVariables = snapshot;
        var secretNames = SecretNamesProvider?.Invoke();
        if (secretNames is not null) editor.SecretVariableNames = secretNames;
        var tab = new HttpRequestTabViewModel(editor, request, sourcePath, id) { CollectionPath = collectionPath };
        tab.SetParentContext(collection, folderChain);
        // Saving a draft (no file) bubbles up so the host can promote it into a collection.
        editor.SaveAsRequested += (_, _) => SaveAsRequested?.Invoke(this, tab);
        return tab;
    }

    /// <summary>Raised when the user saves a request that has no backing file (a "+" scratch
    /// draft). The host handles it by promoting the tab into a collection.</summary>
    public event EventHandler<HttpRequestTabViewModel>? SaveAsRequested;

    /// <summary>Creates a new untitled scratch request in <paramref name="workspaceId"/> and opens
    /// it. Scratch tabs are DB-backed drafts (no collection file); the display name auto-increments
    /// (Untitled, Untitled 2, …) among that workspace's existing scratch tabs.</summary>
    public HttpRequestTabViewModel CreateScratch(string? workspaceId)
    {
        var id = "scratch:" + Guid.NewGuid().ToString("N");
        var name = NextUntitledName(workspaceId);
        // Seed with a named RequestItem so the tab adopts that name (a null request would make
        // the tab mirror the URL into its title instead — not what we want for a named draft).
        var seed = new RequestItem { Name = name, Kind = RequestKind.Http, Method = "GET", Url = string.Empty };
        var tab = BuildHttpTab(seed, sourcePath: null, id, collection: null, folderChain: null, collectionPath: null);
        tab.WorkspaceId = workspaceId;
        tab.IsScratch = true;
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Next free "Untitled" / "Untitled N" display name among the scratch tabs already
    /// open in <paramref name="workspaceId"/>.</summary>
    public string NextUntitledName(string? workspaceId)
    {
        bool Taken(string candidate) => Tabs.Any(t =>
            t.IsScratch
            && string.Equals(t.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken("Untitled")) return "Untitled";
        for (var i = 2; i < 100_000; i++)
        {
            var candidate = "Untitled " + i;
            if (!Taken(candidate)) return candidate;
        }
        return "Untitled";
    }

    /// <summary>Reinstates a request tab from a persisted full-state blob (a dirty edit or a
    /// scratch draft), preserving the exact in-progress content + dirty flag. The display
    /// <paramref name="name"/> is taken from the saved row so in-memory renames survive.</summary>
    public HttpRequestTabViewModel RestoreHttpTab(
        RequestItem item, string id, string? sourcePath, string? collectionPath,
        string? workspaceId, bool isScratch, bool isDirty, string name)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is HttpRequestTabViewModel e) return e;

        var tab = BuildHttpTab(item, sourcePath, id, collection: null, folderChain: null, collectionPath: collectionPath);
        tab.WorkspaceId = workspaceId;
        tab.IsScratch = isScratch;
        tab.Name = name;
        tab.Editor.IsDirty = isDirty; // mirrors onto tab.IsDirty via the editor->tab binding
        Tabs.Add(tab);
        return tab;
    }

    /// <summary>Full snapshot of every restorable tab for the session DB. Request + scratch tabs
    /// carry a JSON state blob when dirty or scratch (so unsaved edits survive verbatim); clean
    /// file-backed tabs store only their path and are rebuilt from disk. Transient kinds (runner,
    /// diff, environment, workspace editor) are skipped.</summary>
    public IReadOnlyList<TabFullSnapshot> FullSnapshot()
    {
        var rows = new List<TabFullSnapshot>();
        for (var i = 0; i < Tabs.Count; i++)
        {
            var t = Tabs[i];
            var isHistory = t is HistoryTabViewModel;
            var restorable = isHistory
                || (t is HttpRequestTabViewModel)
                || (t is SoapRequestTabViewModel && !string.IsNullOrEmpty(t.SourcePath));
            if (!restorable) continue;

            string? blob = null;
            if (!isHistory && t is HttpRequestTabViewModel http && (t.IsDirty || t.IsScratch))
            {
                try { blob = System.Text.Json.JsonSerializer.Serialize(http.Editor.BuildRequestItemFromVm()); }
                catch { blob = null; }
            }

            rows.Add(new TabFullSnapshot(
                Id: t.Id,
                WorkspaceId: t.WorkspaceId,
                CollectionPath: t.CollectionPath,
                SourcePath: t.SourcePath,
                Name: t.Name,
                Kind: t.Kind,
                OrderIndex: i,
                IsActive: ReferenceEquals(t, ActiveTab),
                IsDirty: t.IsDirty,
                IsScratch: t.IsScratch,
                StateBlob: blob));
        }
        return rows;
    }

    /// <summary>Opens an empty draft tab (legacy / fallback path when no collection is active).
    /// The optional <paramref name="collectionPath"/> stamps the draft so it shows up only under
    /// that collection's tab strip; when omitted it defaults to the <see cref="ActiveScope"/> so a
    /// draft created while a collection is active doesn't leak into every other collection.</summary>
    public RequestTabViewModel OpenDraft(RequestKind kind = RequestKind.Http, string? collectionPath = null)
    {
        collectionPath ??= ActiveScope;
        var id = "draft:" + Guid.NewGuid().ToString("N");

        RequestTabViewModel tab;
        if (kind == RequestKind.Soap && _soapFactory is not null)
        {
            tab = new SoapRequestTabViewModel(_soapFactory(), request: null, sourcePath: null, id)
            {
                CollectionPath = collectionPath,
            };
        }
        else
        {
            var editor = _editorFactory();
            if (WorkspaceContextProvider is not null) editor.WorkspaceContextProvider = WorkspaceContextProvider;
            // Seed env vars on the empty draft so a user typing {{var}} into a new request
            // sees it resolve immediately, not after the next env-change event.
            var snapshot = EnvironmentSnapshotProvider?.Invoke();
            if (snapshot is not null) editor.EnvironmentVariables = snapshot;
            var secretNames = SecretNamesProvider?.Invoke();
            if (secretNames is not null) editor.SecretVariableNames = secretNames;
            var http = new HttpRequestTabViewModel(editor, request: null, sourcePath: null, id)
            {
                CollectionPath = collectionPath,
            };
            http.Kind = kind;
            tab = http;
        }

        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    [RelayCommand]
    public void CloseTab(RequestTabViewModel? tab)
    {
        if (tab is null) return;
        // Neighbor pick uses the VISIBLE list so closing a tab keeps focus inside the active
        // collection's scope (otherwise we'd flip to a hidden tab and the user sees nothing).
        var visibleIndex = VisibleTabs.IndexOf(tab);
        var indexInAll = Tabs.IndexOf(tab);
        if (indexInAll < 0) return;

        Tabs.RemoveAt(indexInAll);
        if (ActiveTab == tab)
        {
            if (VisibleTabs.Count == 0) ActiveTab = null;
            else if (visibleIndex >= 0 && visibleIndex < VisibleTabs.Count) ActiveTab = VisibleTabs[visibleIndex];
            else ActiveTab = VisibleTabs[^1];
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveTab))]
    private void CloseActiveTab() => CloseTab(ActiveTab);

    private bool HasActiveTab() => ActiveTab is not null;

    // The bulk close commands below operate on VISIBLE tabs (the active collection's scope) so a
    // "Close All" from a tab's right-click menu only clears what the user can actually see — tabs
    // belonging to other collections stay put. Each delegates to CloseTab so active-tab repointing
    // happens uniformly. We snapshot the target list first because CloseTab mutates VisibleTabs as
    // it goes.

    [RelayCommand]
    public void CloseAll()
    {
        foreach (var t in VisibleTabs.ToList()) CloseTab(t);
    }

    [RelayCommand]
    public void CloseOthers(RequestTabViewModel? keep)
    {
        if (keep is null) return;
        foreach (var t in VisibleTabs.Where(t => !ReferenceEquals(t, keep)).ToList()) CloseTab(t);
        if (Tabs.Contains(keep)) ActiveTab = keep;
    }

    /// <summary>Closes every visible tab positioned to the left of <paramref name="tab"/>.</summary>
    [RelayCommand]
    public void CloseToLeft(RequestTabViewModel? tab)
    {
        if (tab is null) return;
        var idx = VisibleTabs.IndexOf(tab);
        if (idx <= 0) return;
        foreach (var t in VisibleTabs.Take(idx).ToList()) CloseTab(t);
        if (Tabs.Contains(tab)) ActiveTab = tab;
    }

    /// <summary>Closes every visible tab positioned to the right of <paramref name="tab"/>.</summary>
    [RelayCommand]
    public void CloseToRight(RequestTabViewModel? tab)
    {
        if (tab is null) return;
        var idx = VisibleTabs.IndexOf(tab);
        if (idx < 0) return;
        foreach (var t in VisibleTabs.Skip(idx + 1).ToList()) CloseTab(t);
        if (Tabs.Contains(tab)) ActiveTab = tab;
    }

    /// <summary>Closes every visible tab with no unsaved changes, keeping the dirty ones.</summary>
    [RelayCommand]
    public void CloseSaved()
    {
        foreach (var t in VisibleTabs.Where(t => !t.IsDirty).ToList()) CloseTab(t);
    }

    [RelayCommand]
    public void Activate(RequestTabViewModel? tab)
    {
        if (tab is not null && Tabs.Contains(tab)) ActiveTab = tab;
    }

    /// <summary>Opens a new Collection Runner tab for <paramref name="collection"/>. Each
    /// invocation creates a fresh tab (GUID id) so the user can compare two runs side-by-side
    /// without losing prior results. <paramref name="collectionPath"/> stamps the tab so it
    /// stays under that scope when the user navigates.</summary>
    public CollectionRunTabViewModel OpenRunTab(
        Vegha.Core.Domain.Collection collection,
        Vegha.Core.Requests.HttpExecutor http,
        Vegha.Core.Scripting.JintHost scripting,
        Vegha.Core.Requests.RequestComposition.WorkspaceContext? workspace = null,
        string? collectionPath = null,
        Vegha.Integrations.Secrets.SecretRegistry? secretRegistry = null)
    {
        var id = "run:" + Guid.NewGuid().ToString("N");
        var tab = new CollectionRunTabViewModel(collection, id, http, scripting, workspace, secretRegistry)
        {
            CollectionPath = collectionPath,
        };
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Opens (or activates) the workspace editor tab. There's only ever one editor
    /// per workspace, identified by the workspace folder path.</summary>
    public WorkspaceTabViewModel OpenWorkspaceTab(WorkspaceItemViewModel ws)
    {
        var id = "workspace:" + ws.FolderPath;
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is WorkspaceTabViewModel wsTab) { ActiveTab = wsTab; return wsTab; }

        var tab = new WorkspaceTabViewModel(ws, id);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Closes the workspace editor tab for the given workspace path, if it's open.
    /// The host calls this on workspace switch so a stale workspace's editor doesn't linger
    /// in the tab strip after the user navigates away.</summary>
    public void CloseWorkspaceTab(string workspaceFolderPath)
    {
        var id = "workspace:" + workspaceFolderPath;
        var match = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is not null) CloseTab(match);
    }

    /// <summary>Opens (or activates) a tab showing a historical request + response. The
    /// editor is hydrated from the persisted request blob (preserving <c>{{var}}</c> strings
    /// intact), then the response fields are populated directly from the row so the response
    /// pane renders without invoking <c>SendAsync</c>. Clicking the same history row twice
    /// activates the existing tab — no duplicates.</summary>
    public HistoryTabViewModel OpenHistoryTab(HistoryReplayPayload payload)
    {
        var id = HistoryTabViewModel.BuildId(payload.Row.Id);
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (existing is HistoryTabViewModel ht) { ActiveTab = ht; return ht; }

        var editor = _editorFactory();
        // Request side: prefer the persisted blob (full state) and fall back to method/url
        // when payload persistence was off when the row was recorded.
        if (!string.IsNullOrEmpty(payload.RequestBlob))
        {
            editor.ApplyRequestBlob(payload.RequestBlob);
        }
        else
        {
            editor.Method = payload.Row.Method;
            editor.Url = payload.Row.Url;
        }
        // Response side: populate from the row so the response pane renders the historical
        // response immediately. Headers / cookies aren't persisted today, so those sub-tabs
        // are empty — acceptable given the request body + status + duration cover the most-
        // asked questions. The user can click Send in the tab to actually replay.
        editor.ResponseStatusCode = payload.Row.StatusCode;
        editor.ResponseStatusText = string.Empty;
        editor.ResponseBody = payload.ResponseBody ?? string.Empty;
        editor.ResponseElapsedMilliseconds = payload.Row.DurationMs;
        editor.ErrorMessage = payload.Row.ErrorMessage;
        editor.HasResponse = true;
        // History tabs are read-only replays — direct property writes above bypass the
        // _loading gate inside the editor and mark IsDirty. Clear it so the app-close
        // unsaved-changes prompt doesn't fire on a freshly restored history tab.
        editor.IsDirty = false;

        var tab = new HistoryTabViewModel(editor, payload.Row.Id);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Opens (or activates) a tab editing the given environment. Saves go
    /// through <paramref name="saveAsync"/> so the host can write through CollectionStore.</summary>
    public RequestTabViewModel OpenEnvironmentTab(
        Vegha.Core.Domain.Environment env,
        Func<Vegha.Core.Domain.Environment, Task> saveAsync,
        string workspaceId,
        string? collectionPath = null)
    {
        var id = "env:" + workspaceId + ":" + env.Name;
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (existing is not null) { ActiveTab = existing; return existing; }

        var tab = new EnvironmentTabViewModel(env, id, saveAsync) { CollectionPath = collectionPath };
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Snapshot for the session store: id + source path + collection + active flag.</summary>
    public IReadOnlyList<TabSnapshot> Snapshot()
    {
        return Tabs.Select(t => new TabSnapshot(
            Id: t.Id,
            SourcePath: t.SourcePath,
            Name: t.Name,
            Kind: t.Kind,
            IsActive: t == ActiveTab,
            CollectionPath: t.CollectionPath)).ToList();
    }

    /// <summary>Re-hydrates open tabs from a persisted snapshot. Bru-backed entries (request
    /// tabs) require a source path that still exists on disk; history entries are identified
    /// by the <c>"history:"</c> id prefix and resolved via <paramref name="historyResolver"/>.
    /// Missing rows (deleted files, pruned history) are silently dropped. Returns the count
    /// of tabs that were actually opened.</summary>
    public async Task<int> LoadFromStoreAsync(
        IReadOnlyList<TabSnapshot> entries,
        Func<string, Task<RequestItem?>> resolver,
        Func<long, Task<HistoryReplayPayload?>>? historyResolver = null,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return 0;

        // Split snapshots by kind. Bru-backed entries need a live source path; history
        // entries are matched by Id prefix and resolved via HistoryStore.
        // NOTE: do not use ConfigureAwait(false) anywhere in this method. The continuation
        // after Task.WhenAll mutates an ObservableCollection and the UI-bound ActiveTab
        // property; dropping the sync context would resume on a worker thread and cross-
        // thread Avalonia property writes (e.g. RowDefinition.MinHeight) crash.
        var historySnapshots = entries
            .Where(e => HistoryTabViewModel.TryParseId(e.Id, out _))
            .ToList();
        var resolvable = entries
            .Where(e => !HistoryTabViewModel.TryParseId(e.Id, out _))
            .Where(e => !string.IsNullOrEmpty(e.SourcePath) && System.IO.File.Exists(e.SourcePath))
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // Resolve bru-file requests in parallel.
        var resolveTasks = resolvable
            .Select(e => SafeResolve(resolver, e.SourcePath, _logger))
            .ToArray();
        var items = resolveTasks.Length > 0 ? await Task.WhenAll(resolveTasks) : Array.Empty<RequestItem?>();

        // Resolve history payloads in parallel (when a resolver was supplied).
        Task<HistoryReplayPayload?>[] historyTasks = historyResolver is null || historySnapshots.Count == 0
            ? Array.Empty<Task<HistoryReplayPayload?>>()
            : historySnapshots.Select(e =>
            {
                HistoryTabViewModel.TryParseId(e.Id, out var historyId);
                return SafeResolveHistory(historyResolver, historyId, _logger);
            }).ToArray();
        var payloads = historyTasks.Length > 0 ? await Task.WhenAll(historyTasks) : Array.Empty<HistoryReplayPayload?>();

        cancellationToken.ThrowIfCancellationRequested();

        var opened = 0;
        for (var i = 0; i < resolvable.Count; i++)
        {
            var item = items[i];
            if (item is null) continue;
            var entry = resolvable[i];
            OpenOrActivate(item, entry.SourcePath!, collectionPath: entry.CollectionPath);
            opened++;
        }
        for (var i = 0; i < historySnapshots.Count; i++)
        {
            var payload = payloads[i];
            if (payload is null) continue;
            OpenHistoryTab(payload);
            opened++;
        }

        if (opened == 0) return 0;

        // Restore the active selection. History tabs match on Id ("history:<id>"); bru tabs
        // historically matched on SourcePath, so try that first and fall back to Id.
        var active = entries.FirstOrDefault(e => e.IsActive);
        if (active is not null)
        {
            var match = Tabs.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(active.SourcePath)
                    && string.Equals(t.SourcePath, active.SourcePath, StringComparison.OrdinalIgnoreCase))
                ?? Tabs.FirstOrDefault(t =>
                    string.Equals(t.Id, active.Id, StringComparison.OrdinalIgnoreCase));
            if (match is not null) ActiveTab = match;
        }
        return opened;
    }

    private static async Task<HistoryReplayPayload?> SafeResolveHistory(
        Func<long, Task<HistoryReplayPayload?>> resolver,
        long historyId,
        ILogger logger)
    {
        try
        {
            return await resolver(historyId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Skipping unrestorable history tab {Id}", historyId);
            return null;
        }
    }

    private static async Task<RequestItem?> SafeResolve(
        Func<string, Task<RequestItem?>> resolver,
        string? sourcePath,
        ILogger logger)
    {
        try
        {
            return await resolver(sourcePath!);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Skipping unrestorable tab {Path}", sourcePath);
            return null;
        }
    }
}

public sealed record TabSnapshot(
    string Id,
    string? SourcePath,
    string Name,
    RequestKind Kind,
    bool IsActive,
    string? CollectionPath = null);

/// <summary>Full per-tab snapshot for the session DB — adds workspace, ordering, dirty/scratch
/// flags, and an optional full-state JSON blob to <see cref="TabSnapshot"/>. The host maps this
/// to the persistence layer's row type (keeping the ViewModels project free of a persistence
/// dependency).</summary>
public sealed record TabFullSnapshot(
    string Id,
    string? WorkspaceId,
    string? CollectionPath,
    string? SourcePath,
    string Name,
    RequestKind Kind,
    int OrderIndex,
    bool IsActive,
    bool IsDirty,
    bool IsScratch,
    string? StateBlob);
