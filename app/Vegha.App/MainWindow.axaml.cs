using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.Controls.Services;
using Vegha.App.Controls.Workspace;
using Vegha.App.Services;
using Vegha.App.ViewModels;
using Vegha.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Vegha.App;

public partial class MainWindow : Window
{
    private LayoutSettingsStore? _layoutStore;
    private AppSettingsStore? _appSettingsStore;

    public MainWindow()
    {
        InitializeComponent();
        ApplyMenuShortcutGestures();
        Opened += (_, _) =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[startup] window opened at {Program.StartupClock.ElapsedMilliseconds}ms");
            // Wrap our content so the interface-zoom factor scales every control.
            ZoomHost.Attach(this);
        };
        Loaded += OnLoaded;
        Closing += OnClosing;

        // Ctrl/Cmd+K opens the search palette anywhere in the window. Tunnel routing so
        // we win against TextBox key handlers.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.K && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta))
            {
                OpenSearchPalette();
                e.Handled = true;
            }
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Activity rail Settings + Help.
        Rail.SettingsRequested += async (_, _) => await OpenSettingsDialogAsync();
        Rail.HelpRequested += (_, _) => ShowHelp();

        // Top-bar Import.
        TopBar.ImportRequested += async (_, _) => await OpenImportWizardAsync();
        // Top-bar Open collection — route through WorkspacesViewModel so the path is
        // persisted in the active workspace (otherwise it disappears on next launch).
        TopBar.OpenCollectionRequested += (_, path) =>
            App.Services.GetService<WorkspacesViewModel>()?.LinkCollection(path);
        // Env picker's Configure button → route per scope. Collection envs surface in the
        // left activity rail's Environments panel; global envs require the workspace editor
        // dialog (where workspace-scoped envs are persisted).
        TopBar.ConfigureEnvsRequested += (_, scope) =>
        {
            if (scope == Vegha.App.Controls.Shell.EnvScope.Collection)
            {
                if (DataContext is MainWindowViewModel mwvm)
                    mwvm.ActiveSidebarSection = "environments";
                return;
            }
            var workspaces = App.Services.GetService<WorkspacesViewModel>();
            if (workspaces?.ActiveWorkspace is { } ws)
                OpenWorkspaceEditorTab(ws, preselectEnvironments: true);
        };
        // Collections sidebar "+" → Import collection routes through here too so the
        // unified wizard is the single import entry point.
        CollectionsPanelControl.ImportRequested += async (_, _) => await OpenImportWizardAsync();
        // Collections sidebar "+" → Open collection. Persist the chosen folder in the
        // active workspace so it survives a restart; without this the collection only
        // shows up until the app closes.
        CollectionsPanelControl.OpenCollectionRequested += (_, path) =>
            App.Services.GetService<WorkspacesViewModel>()?.LinkCollection(path);
        // The codegen toggle now lives next to the Save button inside RequestEditor (one
        // per tab, instantiated through a DataTemplate). It raises a bubbling RoutedEvent
        // that we catch at the window level so we don't have to re-subscribe per tab.
        AddHandler(
            Vegha.App.Controls.Workspace.RequestEditor.CodegenToggleRequestedEvent,
            (_, _) => SetCodePanelCollapsed(!_codegenCollapsed));

        // Edit button inside the Manage-Workspaces dialog bubbles up here — open a tab
        // for the picked workspace and seed it with the current collections + envs.
        AddHandler(
            Vegha.App.Controls.Shell.AppTitleBar.WorkspaceEditRequestedEvent,
            (_, e) => OpenWorkspaceEditorTab(e.Workspace));

        // (Earlier: auto-close stale workspace tabs on workspace switch. Workspace editing is
        // now a modal dialog — no in-memory tab to clean up.)

        // Welcome card buttons (only relevant when no tabs are open).
        WelcomeCard.NewRequestRequested += (_, _) => CreateScratchRequest();
        WelcomeCard.OpenCollectionRequested += async (_, _) => await OpenCollectionFolderAsync();
        WelcomeCard.ImportRequested += async (_, _) => await OpenImportWizardAsync();

        // Tab strip: "+" / per-tab right-click menu actions that need host services (file IO,
        // dialogs, the scratch + collection stores) bubble up here.
        RequestTabStripControl.NewRequestRequested += (_, _) => CreateScratchRequest();
        RequestTabStripControl.CloneRequested += (_, tab) => CloneTabToScratch(tab);
        RequestTabStripControl.RenameRequested += async (_, tab) => await RenameTabAsync(tab);
        RequestTabStripControl.RevertRequested += async (_, tab) => await RevertTabAsync(tab);
        RequestTabStripControl.SaveToCollectionRequested += async (_, tab) => await SaveTabToCollectionAsync(tab);

        // Codegen panel close → collapse right column (View menu / </> button re-opens).
        CodegenPaneRef.CloseRequested += (_, _) => SetCodePanelCollapsed(true);
        ShowCodePanelMenu.Header = "Hide code panel";
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // === EAGER: needed for first paint ===
        // Resolve stores from DI (safe: App.OnFrameworkInitializationCompleted set Services before
        // the MainWindow ctor was reached).
        _layoutStore = App.Services.GetService<LayoutSettingsStore>();
        _appSettingsStore = App.Services.GetService<AppSettingsStore>();
        var settings = _layoutStore?.Load() ?? LayoutSettings.Default;
        ApplyLayout(settings);

        // Apply theme (mode + named variant), font size, editor prefs, and the persisted
        // interface zoom factor from AppSettings.
        var appSettings0 = _appSettingsStore?.Load() ?? AppSettings.Default;
        ApplyAllAppSettings(appSettings0);

        if (DataContext is MainWindowViewModel mwvm && _appSettingsStore is not null)
            mwvm.AttachSettingsStore(_appSettingsStore);

        // Subscribe to the store's Changed event so anything that calls Save (the settings
        // dialog, the zoom commands) gets reflected in HttpExecutor / theme / history.
        if (_appSettingsStore is not null)
            _appSettingsStore.Changed += s => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyAllAppSettings(s));

        // Populate the secret registry from the configured providers so secret://… URIs
        // resolve at request time and the env editor's provider picker is filled.
        ReloadSecretProviders();

        // Collapse the response-pane row when the active tab isn't an HTTP request (workspace
        // editor tabs, env editor tabs — they fill the full area). IsVisible alone leaves a
        // 360px gap because the row keeps its height.
        var tabsForResponse = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (tabsForResponse is not null)
        {
            ApplyResponseRowHeight(tabsForResponse.IsResponsePaneApplicable);
            tabsForResponse.PropertyChanged += (_, evt) =>
            {
                if (evt.PropertyName == nameof(Vegha.App.ViewModels.Tabs.OpenTabsViewModel.IsResponsePaneApplicable))
                    ApplyResponseRowHeight(tabsForResponse.IsResponsePaneApplicable);
            };
        }

        // Tab persistence is DB-backed: the full editor state of every open tab (including
        // unsaved/dirty edits and untitled scratch drafts) is snapshotted to tabs.db at
        // checkpoints — structural changes (open/close/reorder), collection switch, workspace
        // switch, and app close — so nothing is lost across switches or restarts. We deliberately
        // do NOT persist on every keystroke; in-memory editor state is authoritative between
        // checkpoints.
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        var tabStateStore = App.Services.GetService<Vegha.Core.Persistence.TabStateStore>();
        var collectionsForTabs = App.Services.GetService<CollectionsViewModel>();
        var workspacesForTabs = App.Services.GetService<WorkspacesViewModel>();
        // Saving a scratch ("+") draft has no on-disk home — promote it into a collection.
        if (tabs is not null)
            tabs.SaveAsRequested += async (_, tab) => await SaveTabToCollectionAsync(tab);

        if (tabs is not null && tabStateStore is not null)
        {
            // Structural change (open/close/reorder) → persist. This also reflects a closed
            // scratch tab being dropped from the DB.
            tabs.TabsChanged += (_, _) => PersistTabs(tabs, tabStateStore);

            // Collection switch → persist (durability; tabs stay in memory, filtered by scope).
            if (collectionsForTabs is not null)
                collectionsForTabs.ActiveCollectionChanged += (_, _) => PersistTabs(tabs, tabStateStore);

            // Workspace switch → repoint the scratch scope and persist. Tabs stay in memory and
            // are filtered by ActiveWorkspaceId (scratch) / ActiveScope (collection), so dirty
            // edits survive a round-trip without touching disk.
            if (workspacesForTabs is not null)
                workspacesForTabs.PropertyChanged += (_, evt) =>
                {
                    if (evt.PropertyName != nameof(WorkspacesViewModel.ActiveWorkspace)) return;
                    tabs.ActiveWorkspaceId = workspacesForTabs.ActiveWorkspace?.FolderPath;
                    PersistTabs(tabs, tabStateStore);
                };
        }

        // Source Control: open a diff tab when the user clicks a change row.
        var git = App.Services.GetService<GitViewModel>();
        if (git is not null && tabs is not null)
        {
            git.OpenDiffRequested += (_, row) => OpenGitDiffTab(git, row, tabs);
            git.OpenFileRequested += (_, row) => OpenFileExternally(git, row);
        }
        // Source Control: "Set git user.name and user.email" inline banner → prompt + write.
        GitPanelControl.ConfigureIdentityRequested += async (_, _) =>
        {
            if (git is null) return;
            await ShowGitIdentityDialogAsync(git);
        };
        // Source Control: route the panel's own row-click event to the same diff-tab opener.
        GitPanelControl.OpenDiffRequested += (_, row) =>
        {
            if (git is null || tabs is null) return;
            OpenGitDiffTab(git, row, tabs);
        };

        // Wire the credentials-prompt fallback so libgit2sharp's network ops can surface a
        // dialog when git credential fill yields nothing. The dialog must marshal back to the
        // UI thread because libgit2 calls the handler on a worker.
        var creds = App.Services.GetService<Vegha.Integrations.Git.GitCredentialsService>();
        if (creds is not null)
        {
            creds.PromptFallback = req =>
            {
                return global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var dlg = new Vegha.App.Controls.Workspace.CredentialsPromptDialog(req.Url, req.UsernameHint);
                    var ok = await dlg.ShowDialog<bool>(this);
                    if (!ok || string.IsNullOrEmpty(dlg.Username) || string.IsNullOrEmpty(dlg.Secret))
                        return null;
                    return new Vegha.Integrations.Git.CredentialsResponse(dlg.Username!, dlg.Secret!, dlg.Remember);
                }).GetAwaiter().GetResult();
            };
        }

        // Show the "Loading workspace…" skeleton until the deferred apply completes.
        var collectionsForSkeleton = App.Services.GetService<CollectionsViewModel>();
        var workspacesForApply = App.Services.GetService<WorkspacesViewModel>();
        if (collectionsForSkeleton is not null) collectionsForSkeleton.IsLoading = true;

        // First-run command-line folder load — keep on the eager path because the user
        // explicitly passed the folder; defer would feel slower than expected.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
        {
            collectionsForSkeleton?.LoadFromDirectory(args[1]);
        }

        // === DEFERRED block A: workspace tree → restore tabs → env push → welcome ===
        // Runs at Background priority so the window paints first. Order matters:
        //   1. ApplyActiveAsync populates Collections.Roots off-thread (needed before tab
        //      restore can resolve CollectionPath references) and manages the IsLoading
        //      skeleton itself, dropping it when the tree is ready.
        //   2. RestoreOpenTabsAsync rehydrates tabs (now parallelized — see OpenTabsViewModel).
        //   3. PushEnvironmentToOpenTabs feeds restored tabs the current env snapshot so
        //      {{var}} resolves without a close/reopen.
        //   4. Welcome dialog last, so it appears over the fully-painted main UI.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // ApplyActiveAsync owns the IsLoading skeleton for the has-workspace path —
                // it loads collections off-thread and drops IsLoading when done (or when a
                // later switch supersedes it). Only the no-workspace case needs an explicit
                // drop here so the skeleton doesn't linger over an empty sidebar.
                if (workspacesForApply?.ActiveWorkspace is not null)
                    await workspacesForApply.ApplyActiveAsync(workspacesForApply.ActiveWorkspace);
                else if (collectionsForSkeleton is not null)
                    collectionsForSkeleton.IsLoading = false;
                System.Diagnostics.Debug.WriteLine(
                    $"[startup] workspace applied at {Program.StartupClock.ElapsedMilliseconds}ms");

                await RestoreOpenTabsAsync();
                collectionsForSkeleton?.PushEnvironmentToOpenTabs();
                System.Diagnostics.Debug.WriteLine(
                    $"[startup] tabs restored at {Program.StartupClock.ElapsedMilliseconds}ms");

                var appSettings = _appSettingsStore?.Load() ?? AppSettings.Default;
                if (!appSettings.WelcomeShown) await ShowWelcomeAsync(appSettings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[startup] deferred init failed: {ex}");
                // IsLoading is already owned + reset by ApplyActiveAsync (or the no-workspace
                // branch above); nothing to drop here.
            }
        }, global::Avalonia.Threading.DispatcherPriority.Background);

        // === DEFERRED block B: event wiring for features that don't paint immediately ===
        // History dialogs, Collections/Envs/Workspaces interactions — every subscription
        // resolves a DI service we don't otherwise need at first paint.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var history = App.Services.GetService<HistoryViewModel>();
            var workspacesForHistory = App.Services.GetService<WorkspacesViewModel>();
            if (history is not null)
            {
                history.HarExportRequested += async (_, _) => await SaveHarAsync(history);
                history.OpenInTabRequested += (_, payload) => OpenHistoryTabFromPayload(payload);
                history.OpenAsRequestRequested += (_, payload) => OpenHistoryAsScratchFromPayload(payload);
                history.SaveToCollectionRequested += async (_, payload) => await SaveHistoryToCollectionAsync(payload);

                // Scope history to the active workspace and keep it in sync on every switch. A
                // one-time backfill (awaited before scoping) moves any pre-migration (unscoped)
                // rows into the active workspace so existing history doesn't disappear under the
                // new filter or flash empty before the migration lands.
                var activeWs = workspacesForHistory?.ActiveWorkspace?.FolderPath;
                _ = InitializeHistoryWorkspaceAsync(history, activeWs);
                if (workspacesForHistory is not null)
                    workspacesForHistory.PropertyChanged += (_, evt) =>
                    {
                        if (evt.PropertyName == nameof(WorkspacesViewModel.ActiveWorkspace))
                            history.WorkspaceId = workspacesForHistory.ActiveWorkspace?.FolderPath;
                    };
            }

            // Runner sidebar: clicking "Run" should open a CollectionRunTab.
            var runner = App.Services.GetService<Vegha.App.ViewModels.Runner.RunnerSidebarViewModel>();
            if (runner is not null)
            {
                runner.OpenRunRequested = root => OpenCollectionRunTab(root);
            }

            var collectionsForNewReq = App.Services.GetService<CollectionsViewModel>();
            if (collectionsForNewReq is not null)
            {
                collectionsForNewReq.NewRequestRequested += async (_, node) => await OpenNewRequestDialogAsync(collectionsForNewReq, node);
            }

            var envs = App.Services.GetService<EnvironmentsViewModel>();
            var openTabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
            var workspaces = App.Services.GetService<WorkspacesViewModel>();
            // EditRequested used to open a dedicated EnvironmentTabViewModel in the main tab
            // strip. The Environments view is now a master/detail surface that overlays the
            // sidebar + workspace columns, so variable editing happens inline against
            // SelectedEnvironment — no tab needed. The event still exists on the VM (back-
            // compat) but is intentionally left unsubscribed here.

            if (workspaces is not null)
            {
                UpdateCreateCollectionDefault(workspaces.ActiveWorkspace);
                workspaces.PropertyChanged += (_, evt) =>
                {
                    if (evt.PropertyName == nameof(WorkspacesViewModel.ActiveWorkspace))
                        UpdateCreateCollectionDefault(workspaces.ActiveWorkspace);
                };
            }
        }, global::Avalonia.Threading.DispatcherPriority.Background);

        // === DEFERRED block C: auto-update checker ===
        // Past first paint so the GitHub feed poll never delays startup. Hides the
        // "Check for Updates…" menu item on builds that can't self-update (store flavors +
        // uninstalled dev runs), then kicks the silent startup check + periodic poll. The VM
        // honors the AutoCheckForUpdates setting and is a no-op when unsupported.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MainWindowViewModel mwvm) return;
            MenuCheckUpdates.IsVisible = mwvm.Update.IsSupported;
            mwvm.Update.StartBackgroundChecks();
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdateCreateCollectionDefault(WorkspaceItemViewModel? ws)
    {
        if (ws is null || string.IsNullOrEmpty(ws.FolderPath))
        {
            CollectionsPanelControl.DefaultCreateCollectionLocation = "";
            return;
        }
        CollectionsPanelControl.DefaultCreateCollectionLocation =
            Path.Combine(ws.FolderPath, "collections");
    }

    private async Task SaveHarAsync(HistoryViewModel history)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export history to HAR",
            SuggestedFileName = $"vegha-{DateTime.Now:yyyyMMdd-HHmm}.har",
        });
        if (file is null) return;
        try
        {
            var har = await history.ExportHarAsync();
            await using var stream = await file.OpenWriteAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes(har);
            await stream.WriteAsync(bytes);
        }
        catch { /* best-effort */ }
    }

    private void OpenHistoryTabFromPayload(HistoryReplayPayload payload)
    {
        var tabs = App.Services?.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        tabs?.OpenHistoryTab(payload);
    }

    /// <summary>One-time per-launch: migrate legacy unscoped history rows into the active
    /// workspace, THEN point the History panel at that workspace. Doing the backfill first
    /// avoids the first scoped load returning nothing while the migration is still running.</summary>
    private async Task InitializeHistoryWorkspaceAsync(HistoryViewModel history, string? activeWorkspaceId)
    {
        if (!string.IsNullOrEmpty(activeWorkspaceId))
        {
            var store = App.Services?.GetService<Vegha.Core.History.HistoryStore>();
            if (store is not null)
            {
                try { await store.BackfillWorkspaceAsync(activeWorkspaceId); }
                catch { /* best-effort migration */ }
            }
        }
        history.WorkspaceId = activeWorkspaceId; // triggers the first scoped refresh
    }

    /// <summary>History → "Open as request": promote the entry into an editable scratch draft in
    /// the active workspace and flip the sidebar back to Collections so the (non-history) scratch
    /// tab is actually visible in the strip.</summary>
    private void OpenHistoryAsScratchFromPayload(HistoryReplayPayload payload)
    {
        var tabs = App.Services?.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (tabs is null) return;
        var workspaceId = App.Services?.GetService<WorkspacesViewModel>()?.ActiveWorkspace?.FolderPath
                          ?? tabs.ActiveWorkspaceId;
        tabs.OpenHistoryAsScratch(payload, workspaceId);
        // The scratch tab is a normal request tab, hidden while the History section is active.
        if (DataContext is MainWindowViewModel mwvm)
            mwvm.ActiveSidebarSection = "collections";
    }

    /// <summary>History → "Save to collection…": open the entry as an editable scratch draft and
    /// immediately run the save-to-collection picker on it. Cancelling the picker leaves the
    /// draft open so nothing is lost.</summary>
    private async Task SaveHistoryToCollectionAsync(HistoryReplayPayload payload)
    {
        var tabs = App.Services?.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (tabs is null) return;
        var workspaceId = App.Services?.GetService<WorkspacesViewModel>()?.ActiveWorkspace?.FolderPath
                          ?? tabs.ActiveWorkspaceId;
        var tab = tabs.OpenHistoryAsScratch(payload, workspaceId);
        if (DataContext is MainWindowViewModel mwvm)
            mwvm.ActiveSidebarSection = "collections";
        await SaveTabToCollectionAsync(tab);
    }

    /// <summary>Opens a new Collection Runner tab for <paramref name="root"/>'s collection.
    /// The tab self-owns its execution; this method just wires the DI dependencies and
    /// threads the active environment snapshot in so {{var}} resolves out of the gate.</summary>
    private void OpenCollectionRunTab(Vegha.App.ViewModels.CollectionRootViewModel root)
    {
        if (root.Collection is null) return;
        var tabs = App.Services?.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        var http = App.Services?.GetService<Vegha.Core.Requests.HttpExecutor>();
        var script = App.Services?.GetService<Vegha.Core.Scripting.JintHost>();
        var collectionsVm = App.Services?.GetService<Vegha.App.ViewModels.CollectionsViewModel>();
        if (tabs is null || http is null || script is null) return;

        // Workspace inheritance — workspace-level scripts merge underneath collection. The
        // workspace *environment* is NOT carried here; it is applied through the environment
        // layer (SnapshotMerged below) so the collection environment outranks it.
        Vegha.Core.Requests.RequestComposition.WorkspaceContext? wsCtx = null;
        if (collectionsVm is not null)
        {
            wsCtx = new Vegha.Core.Requests.RequestComposition.WorkspaceContext(
                Variables: null,
                PreRequestScript: collectionsVm.WorkspacePreRequestScript,
                PostResponseScript: collectionsVm.WorkspacePostResponseScript,
                TestsScript: collectionsVm.WorkspaceTestsScript);
        }

        var tab = tabs.OpenRunTab(root.Collection, http, script, wsCtx, root.SourcePath,
            App.Services?.GetService<Vegha.Integrations.Secrets.SecretRegistry>());
        // Initial env snapshot — workspace (global) env underneath the collection env so the
        // collection env wins. Mid-run env changes don't affect an in-flight run by design.
        if (collectionsVm is not null)
            tab.EnvironmentVariables = Vegha.App.ViewModels.CollectionsViewModel.SnapshotMerged(
                collectionsVm.ActiveGlobalEnvironment, collectionsVm.ActiveEnvironment);
    }

    private async Task RestoreOpenTabsAsync()
    {
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        var store = App.Services.GetService<Vegha.Core.Persistence.TabStateStore>();
        var historyStore = App.Services.GetService<Vegha.Core.History.HistoryStore>();
        if (tabs is null || store is null) return;

        // Scope scratch tabs to the active workspace so a restored draft from another workspace
        // stays hidden until the user switches to it.
        tabs.ActiveWorkspaceId = App.Services.GetService<WorkspacesViewModel>()?.ActiveWorkspace?.FolderPath;

        if (historyStore is not null)
        {
            try { await historyStore.PruneAsync(); }
            catch { /* best-effort */ }
        }

        try
        {
            // Reinstate each tab in saved order:
            //   • history tabs  → resolved from the history DB
            //   • blob present  → reinstated verbatim (dirty edits + scratch drafts), keeping dirty
            //   • file-backed   → parsed fresh from disk (clean collection tabs stay in sync with disk)
            var jsonOpts = new System.Text.Json.JsonSerializerOptions();
            string? activeId = null;
            foreach (var r in store.LoadAll())
            {
                if (r.IsActive) activeId = r.Id;
                try
                {
                    if (Vegha.App.ViewModels.Tabs.HistoryTabViewModel.TryParseId(r.Id, out var historyId))
                    {
                        if (historyStore is null) continue;
                        var entry = await historyStore.GetByIdAsync(historyId);
                        if (entry is null) continue;
                        var hblob = await historyStore.GetRequestBlobAsync(historyId);
                        tabs.OpenHistoryTab(new HistoryReplayPayload(
                            Row: HistoryRow.From(entry), RequestBlob: hblob, ResponseBody: entry.ResponseBodyPreview));
                    }
                    else if (!string.IsNullOrEmpty(r.StateBlob))
                    {
                        var item = System.Text.Json.JsonSerializer.Deserialize<Vegha.Core.Domain.RequestItem>(r.StateBlob, jsonOpts);
                        if (item is null) continue;
                        tabs.RestoreHttpTab(item, r.Id, r.SourcePath, r.CollectionPath,
                            r.WorkspaceId, r.IsScratch, r.IsDirty, r.Name);
                    }
                    else if (!string.IsNullOrEmpty(r.SourcePath) && File.Exists(r.SourcePath))
                    {
                        var item = await ParseBruFromDiskAsync(r.SourcePath);
                        if (item is not null) tabs.OpenOrActivate(item, r.SourcePath, collectionPath: r.CollectionPath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[tabs] skipping unrestorable tab {r.Id}: {ex.Message}");
                }
            }

            // Restore the active selection by id.
            if (activeId is not null)
            {
                var match = tabs.Tabs.FirstOrDefault(t => string.Equals(t.Id, activeId, StringComparison.OrdinalIgnoreCase));
                if (match is not null) tabs.ActiveTab = match;
            }
        }
        finally
        {
            // Enable checkpoint persistence now that the DB has been read — even if it was empty,
            // so a freshly-started session that opens its first tab gets saved.
            _tabPersistenceReady = true;
        }
    }

    private static async Task<Vegha.Core.Domain.RequestItem?> ParseBruFromDiskAsync(string path)
    {
        var bru = await File.ReadAllTextAsync(path);
        var doc = Vegha.Core.Bru.Parser.BruParser.Parse(bru);
        return Vegha.Core.Importers.BruToRequestConverter.Convert(doc);
    }

    /// <summary>Opens a modal WorkspaceEditorDialog for the given workspace, seeded with the
    /// current collections + workspace envs. All in-dialog actions (quick actions, collection
    /// row menus, workspace … menu, env header buttons) route back through callbacks wired
    /// here so the editor view itself stays decoupled from services / sibling dialogs.
    /// When <paramref name="preselectEnvironments"/> is true the dialog opens on the
    /// Environments sub-tab instead of the default Overview (used by the global env
    /// picker's Configure action).</summary>
    private async void OpenWorkspaceEditorTab(
        Vegha.App.ViewModels.WorkspaceItemViewModel ws,
        bool preselectEnvironments = false)
    {
        var collections = App.Services.GetService<CollectionsViewModel>();
        var workspaces = App.Services.GetService<WorkspacesViewModel>();
        if (collections is null || workspaces is null) return;

        // Build a transient tab-VM purely to drive the editor's bindings. It's never
        // registered with OpenTabsViewModel — the dialog owns its lifetime.
        var tab = new Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel(
            ws, id: "workspace-dialog:" + ws.FolderPath);
        tab.SetCollections(collections.AvailableCollections);
        // Reload the workspace env set from disk before binding it. ws.WorkspaceModel is
        // otherwise only refreshed on a full workspace switch, so envs the user added /
        // copied / saved / imported in a previous dialog session were written to disk but
        // never made it back into this cached list — the dialog kept showing the stale
        // snapshot, so a freshly-created env "vanished" on reopen.
        ws.WorkspaceModel = Vegha.Core.FileFormat.WorkspaceModelLoader.Load(ws.FolderPath);
        // Pass the currently-active workspace env as the initial selection so opening the
        // dialog doesn't spuriously activate a different env. With this aligned, the
        // RequestActivateEnvironment hook below only fires when the user picks a new row.
        tab.SetEnvironments(ws.WorkspaceModel.Environments, collections.ActiveGlobalEnvironment);
        if (preselectEnvironments) tab.ActiveSection = "environments";

        // ---- Overview quick actions ----
        tab.RequestCreateCollection = () => TopBar.RaiseCreateCollection();
        tab.RequestOpenCollection   = () => TopBar.RaiseOpenCollection();
        tab.RequestImportCollection = () => TopBar.RaiseImportCollection();
        tab.ActivateCollection      = c => collections.ActiveCollection = c;

        // ---- Workspace header "…" menu ----
        tab.RequestRenameWorkspace          = () => _ = RenameWorkspaceAsync(tab, ws, workspaces);
        tab.RequestRevealWorkspaceInExplorer = () => RevealFolderInExplorer(ws.FolderPath);
        tab.RequestExportWorkspace          = () => ShowNotImplemented(tab, "Export workspace");
        tab.RequestCloseWorkspace           = () => _ = CloseWorkspaceFromEditorAsync(ws, workspaces);

        // ---- Per-collection actions ----
        tab.RequestRenameCollection = c => _ = RenameCollectionAsync(tab, c);
        tab.RequestRemoveCollection = c => RemoveCollectionFromWorkspace(c, ws, workspaces, collections);
        tab.RequestDeleteCollection = c => _ = DeleteCollectionAsync(tab, c, collections);

        // ---- Per-env actions ----
        tab.RequestAddEnvironment    = () => _ = AddWorkspaceEnvAsync(tab, ws);
        tab.RequestRenameEnvironment = env => _ = RenameWorkspaceEnvAsync(tab, env, ws);
        tab.RequestCopyEnvironment   = env => _ = CopyWorkspaceEnvAsync(tab, env, ws);
        tab.RequestDeleteEnvironment = env => _ = DeleteWorkspaceEnvAsync(tab, env, ws);
        tab.RequestSetEnvColor       = env => _ = SetWorkspaceEnvColorAsync(tab, env, ws);
        tab.RequestImportEnvironment = () => _ = ImportWorkspaceEnvAsync(tab, ws);
        // User-driven row click in the workspace dialog flips the shared
        // ActiveGlobalEnvironment so the top-bar pill follows. Skip the no-op case where
        // the selection already matches active — that fires on dialog open when
        // SetEnvironments pre-selects the current env.
        tab.RequestActivateEnvironment = env =>
        {
            if (env is not null && !ReferenceEquals(collections.ActiveGlobalEnvironment, env))
                collections.ActiveGlobalEnvironment = env;
        };

        // Mirror the post-Save state into the shared CollectionsViewModel so the top-bar
        // pill, env picker, and left panel all pick up renames / variable edits / color
        // changes without waiting for a workspace reload. ReplaceEnvironment matches by
        // stable Id and reports whether the env was already tracked; when it wasn't (the
        // "Add Environment" / "Duplicate" paths create an env that lives only inside
        // tab.Environments until the first Save) we append it so it shows up immediately.
        tab.EnvironmentSaved += (_, pair) =>
        {
            if (!collections.ReplaceEnvironment(pair.Old, pair.New))
            {
                collections.GlobalEnvironments.Add(pair.New);
                if (!collections.Environments.Contains(pair.New))
                    collections.Environments.Add(pair.New);
            }
        };

        var dlg = new Vegha.App.Controls.Workspace.WorkspaceEditorDialog
        {
            EditorContext = tab,
        };
        // Close dialog when the user picks Close from the workspace … menu.
        tab.RequestCloseWorkspace = () =>
        {
            _ = CloseWorkspaceFromEditorAsync(ws, workspaces).ContinueWith(t =>
            {
                if (t.Result) global::Avalonia.Threading.Dispatcher.UIThread.Post(() => dlg.Close());
            });
        };
        await dlg.ShowDialog(this);
    }

    /// <summary>"Close workspace" path used from the editor dialog. Returns true if the user
    /// confirmed and the workspace was removed; the dialog caller uses that to dismiss itself.</summary>
    private async Task<bool> CloseWorkspaceFromEditorAsync(
        Vegha.App.ViewModels.WorkspaceItemViewModel ws,
        WorkspacesViewModel workspaces)
    {
        if (ws.IsDefault) return false;
        var confirm = new Vegha.App.Controls.Workspace.CloseWorkspaceDialog(ws.Name, ws.FolderPath);
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return false;
        workspaces.RemoveWorkspaceCommand.Execute(ws);
        return true;
    }

    // ---- Workspace actions ----

    private async Task RenameWorkspaceAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        WorkspaceItemViewModel ws,
        WorkspacesViewModel workspaces)
    {
        var dlg = new Vegha.App.Controls.Workspace.RenameDialog(
            "Rename workspace", "Workspace name", ws.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrEmpty(dlg.ResultName)) return;

        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, ws.Name, StringComparison.Ordinal)) return;

        try
        {
            // Update the manifest on disk + the VM (folder name itself is left alone — Bruno
            // identifies the workspace by manifest, not by folder).
            var manifest = Vegha.Core.Persistence.WorkspaceManifestIO.Read(ws.FolderPath)
                           ?? new Vegha.Core.Persistence.WorkspaceManifest();
            Vegha.Core.Persistence.WorkspaceManifestIO.Write(ws.FolderPath,
                manifest with { Name = newName });
            ws.Name = newName;
            tab.Name = newName;
            // Force a persist of workspaces.json by setting the same property to itself.
            workspaces.ActiveWorkspace = ws;
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Rename failed: {ex.Message}";
        }
    }

    private static void RevealFolderInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", path);
        }
        catch { /* best-effort */ }
    }

    private static void ShowNotImplemented(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab, string feature)
    {
        tab.StatusMessage = feature + " is not implemented yet.";
    }

    // ---- Collection actions ----

    private async Task RenameCollectionAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        CollectionRootViewModel col)
    {
        var dlg = new Vegha.App.Controls.Workspace.RenameDialog(
            "Rename collection", "Collection name", col.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrEmpty(dlg.ResultName)) return;

        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, col.Name, StringComparison.Ordinal)) return;

        try
        {
            // Update the collection.bru meta.name so the tree reloads with the new label.
            var bruPath = System.IO.Path.Combine(col.SourcePath, "collection.bru");
            if (System.IO.File.Exists(bruPath))
            {
                var bru = await System.IO.File.ReadAllTextAsync(bruPath);
                bru = System.Text.RegularExpressions.Regex.Replace(
                    bru, @"(meta\s*\{[^}]*?\bname:\s*)([^\r\n]+)", "$1" + newName);
                await System.IO.File.WriteAllTextAsync(bruPath, bru);
            }
            col.Name = newName;
            tab.StatusMessage = $"Renamed to “{newName}”.";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Rename failed: {ex.Message}";
        }
    }

    private static void RemoveCollectionFromWorkspace(
        CollectionRootViewModel col,
        WorkspaceItemViewModel ws,
        WorkspacesViewModel workspaces,
        CollectionsViewModel collections)
    {
        // If it's a linked collection (outside workspace's collections/ folder), drop the link.
        if (ws.LinkedCollections.Remove(col.SourcePath))
        {
            collections.RemoveCollectionCommand.Execute(col);
            // Triggering persist via the existing Persist path inside RemoveWorkspaceCommand is overkill;
            // re-setting ActiveCollection forces a persist of workspaces.json.
            workspaces.ActiveWorkspace = ws;
        }
        else
        {
            // In-workspace collection — RemoveCollection just removes from the in-memory tree.
            // On next workspace activation it'll come back unless the user Delete'd the folder.
            collections.RemoveCollectionCommand.Execute(col);
        }
    }

    private async Task DeleteCollectionAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        CollectionRootViewModel col,
        CollectionsViewModel collections)
    {
        var owner = this;
        var confirm = new Vegha.App.Controls.Workspace.CloseWorkspaceDialog(
            workspaceName: col.Name,
            workspacePath: col.SourcePath);
        confirm.Title = "Delete collection";
        // The dialog's prompt + descriptor copy is workspace-centric; for collection delete we
        // override the prompt and warning lines so the user knows files will actually be deleted.
        confirm.SetPromptForCollectionDelete();
        var ok = await confirm.ShowDialog<bool>(owner);
        if (!ok) return;

        try
        {
            if (System.IO.Directory.Exists(col.SourcePath))
                System.IO.Directory.Delete(col.SourcePath, recursive: true);
            collections.RemoveCollectionCommand.Execute(col);
            tab.StatusMessage = $"Deleted “{col.Name}” from disk.";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    // ---- Env actions ----

    private async Task RenameWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        Vegha.Core.Domain.Environment env,
        WorkspaceItemViewModel ws)
    {
        var dlg = new Vegha.App.Controls.Workspace.RenameDialog(
            "Rename environment", "Environment name", env.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrEmpty(dlg.ResultName)) return;

        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, env.Name, StringComparison.Ordinal)) return;

        try
        {
            // Sanitize old + new file names the same way the writer does. Without this the
            // old file path can't be found when the original name has chars Path APIs reject,
            // so the new file is written but the old file lingers — the "rename made copies
            // with new names" bug.
            var envDir = System.IO.Path.Combine(ws.FolderPath, Vegha.Core.FileFormat.WorkspaceModelLoader.EnvironmentsFolder);
            System.IO.Directory.CreateDirectory(envDir);
            var oldPath = System.IO.Path.Combine(envDir, SanitizeEnvFileName(env.Name) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix);
            var newPath = System.IO.Path.Combine(envDir, SanitizeEnvFileName(newName) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix);
            var updated = env with { Name = newName };
            await System.IO.File.WriteAllTextAsync(newPath,
                Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(
                    Vegha.Core.FileFormat.EnvironmentFile.FromDomain(updated)));
            if (System.IO.File.Exists(oldPath)
                && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                System.IO.File.Delete(oldPath);

            var idx = tab.Environments.IndexOf(env);
            if (idx >= 0) tab.Environments[idx] = updated;
            tab.SelectedEnvironment = updated;

            // Mirror the swap into the shared CollectionsViewModel state so the top-bar pill,
            // the env-picker popup, and the left-toolbar panel all refresh immediately. Before
            // this call the dialog showed the new name but everywhere else still showed the
            // old name until the user switched collections.
            App.Services.GetService<CollectionsViewModel>()?.ReplaceEnvironment(env, updated);

            tab.StatusMessage = $"Renamed to “{newName}”.";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Rename failed: {ex.Message}";
        }
    }

    /// <summary>Mirrors <see cref="EnvironmentsViewModel"/>'s file-name sanitization so
    /// rename / delete in the workspace dialog target the exact files the writer produced.</summary>
    private static string SanitizeEnvFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>Opens a swatch popup anchored on whatever pill was clicked and persists the
    /// chosen color to the env's .env.json. The same flow the left panel uses — just plumbed
    /// through the WorkspaceTabViewModel because the dialog's env editor lives there.</summary>
    private async Task SetWorkspaceEnvColorAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        Vegha.Core.Domain.Environment env,
        WorkspaceItemViewModel ws)
    {
        var hex = await PickEnvironmentColorAsync();
        if (hex is null) return; // user cancelled
        try
        {
            var dir = System.IO.Path.Combine(ws.FolderPath, Vegha.Core.FileFormat.WorkspaceModelLoader.EnvironmentsFolder);
            System.IO.Directory.CreateDirectory(dir);
            var updated = env with { Color = string.IsNullOrEmpty(hex) ? null : hex };
            var path = System.IO.Path.Combine(dir, SanitizeEnvFileName(updated.Name) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix);
            await System.IO.File.WriteAllTextAsync(path,
                Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(
                    Vegha.Core.FileFormat.EnvironmentFile.FromDomain(updated)));

            // Capture selection before the swap — replacing an item in tab.Environments
            // pushes the ListBox SelectedItem to null (the old reference vanished), so a
            // post-swap ReferenceEquals check would always miss and the env editor empty-
            // states. Mirror the unconditional rename pattern instead.
            var wasSelected = ReferenceEquals(tab.SelectedEnvironment, env);
            var idx = tab.Environments.IndexOf(env);
            if (idx >= 0) tab.Environments[idx] = updated;
            if (wasSelected) tab.SelectedEnvironment = updated;

            App.Services.GetService<CollectionsViewModel>()?.ReplaceEnvironment(env, updated);
            tab.StatusMessage = string.IsNullOrEmpty(hex)
                ? $"Cleared color on “{updated.Name}”."
                : $"Set color on “{updated.Name}”.";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Color update failed: {ex.Message}";
        }
    }

    /// <summary>Workspace env import: same Postman/Bruno detection the left panel uses, but
    /// writes into the workspace env folder rather than the active collection's. Routes the
    /// new env into <see cref="CollectionsViewModel.GlobalEnvironments"/> so the "Global" tab
    /// of the env picker shows it immediately.</summary>
    private async Task ImportWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        WorkspaceItemViewModel ws)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import workspace environment(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new global::Avalonia.Platform.Storage.FilePickerFileType("Environment files") { Patterns = new[] { "*.env.json", "*.postman_environment.json", "*.json" } },
                new global::Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        var paths = picked
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0) return;

        var imported = 0;
        var skipped = 0;
        var dir = System.IO.Path.Combine(ws.FolderPath, Vegha.Core.FileFormat.WorkspaceModelLoader.EnvironmentsFolder);
        System.IO.Directory.CreateDirectory(dir);

        foreach (var path in paths)
        {
            try
            {
                var result = Vegha.Core.Importers.ImportPipeline.DetectAndImportPath(path);
                if (!result.Success || result.Environment is null) { skipped++; continue; }
                var env = result.Environment;

                // Avoid clobbering an existing env file with the same sanitized name.
                var baseName = SanitizeEnvFileName(env.Name);
                var suffix = Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix;
                var fullPath = System.IO.Path.Combine(dir, baseName + suffix);
                for (var n = 2; System.IO.File.Exists(fullPath); n++)
                {
                    var newName = $"{env.Name} {n}";
                    env = env with { Name = newName };
                    fullPath = System.IO.Path.Combine(dir, SanitizeEnvFileName(newName) + suffix);
                }

                await System.IO.File.WriteAllTextAsync(fullPath,
                    Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(
                        Vegha.Core.FileFormat.EnvironmentFile.FromDomain(env)));

                tab.Environments.Add(env);
                var collections = App.Services.GetService<CollectionsViewModel>();
                if (collections is not null)
                {
                    collections.GlobalEnvironments.Add(env);
                    if (!collections.Environments.Contains(env)) collections.Environments.Add(env);
                }
                imported++;
            }
            catch { skipped++; }
        }

        tab.StatusMessage = skipped == 0
            ? $"Imported {imported} environment(s)."
            : $"Imported {imported}, skipped {skipped} (not an environment file).";
    }

    /// <summary>Spawns a transient swatch popup centered on the main window and resolves to
    /// the chosen hex (or empty string for "no color", or null for cancel). Reuses the same
    /// palette the left panel uses so collection + workspace envs share the same palette.</summary>
    private async Task<string?> PickEnvironmentColorAsync()
    {
        var dlg = new Window
        {
            Title = "Pick color",
            Width = 200,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        string? chosen = null;
        var wrap = new global::Avalonia.Controls.WrapPanel { Margin = new global::Avalonia.Thickness(8) };
        foreach (var swatch in Vegha.App.Controls.Shell.EnvironmentColorPalette.Swatches)
        {
            var capture = swatch.Hex;
            var btn = new Button
            {
                Width = 28, Height = 28,
                Margin = new global::Avalonia.Thickness(3),
                Padding = new global::Avalonia.Thickness(0),
                Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(swatch.Hex)),
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(14),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(btn, swatch.Name);
            btn.Click += (_, _) => { chosen = capture; dlg.Close(); };
            wrap.Children.Add(btn);
        }
        var clear = new Button
        {
            Content = "No color",
            Margin = new global::Avalonia.Thickness(8, 0, 8, 8),
            Background = global::Avalonia.Media.Brushes.Transparent,
            BorderThickness = new global::Avalonia.Thickness(0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        clear.Click += (_, _) => { chosen = string.Empty; dlg.Close(); };
        var stack = new StackPanel();
        stack.Children.Add(wrap);
        stack.Children.Add(clear);
        dlg.Content = stack;
        await dlg.ShowDialog(this);
        return chosen;
    }

    /// <summary>Creates a blank workspace env from the "+" button and writes it to
    /// <c>&lt;workspace&gt;/environments/</c> immediately, mirroring the collection-level
    /// "new environment" flow. Persisting up-front (rather than waiting for a Save) is what
    /// makes the env survive a dialog close/reopen.</summary>
    private async Task AddWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        WorkspaceItemViewModel ws)
    {
        var name = NextWorkspaceEnvName(tab, "Untitled");
        var env = new Vegha.Core.Domain.Environment { Id = Guid.NewGuid().ToString("N"), Name = name };
        await PersistNewWorkspaceEnvAsync(tab, ws, env, $"Created “{name}”.");
    }

    private async Task CopyWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        Vegha.Core.Domain.Environment env,
        WorkspaceItemViewModel ws)
    {
        var name = NextWorkspaceEnvName(tab, env.Name + " copy");
        var copy = env with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Variables = env.Variables.Select(v => v with { }).ToList(),
            SecretVariables = env.SecretVariables.ToList(),
        };
        await PersistNewWorkspaceEnvAsync(tab, ws, copy, $"Copied to “{name}”.");
    }

    /// <summary>Picks the first un-taken name with <paramref name="basename"/> as the seed
    /// (then "basename 2", "basename 3", …) against the dialog's current env list.</summary>
    private static string NextWorkspaceEnvName(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab, string basename)
    {
        var taken = new HashSet<string>(tab.Environments.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(basename)) return basename;
        var n = 2;
        while (taken.Contains(basename + " " + n)) n++;
        return basename + " " + n;
    }

    /// <summary>Shared tail for Add / Copy: writes the env's <c>.env.json</c>, then mirrors it
    /// into the dialog list and the shared <see cref="CollectionsViewModel"/> scope lists so
    /// the top-bar pill / env picker pick it up immediately. Selecting it activates it via the
    /// tab's RequestActivateEnvironment hook.</summary>
    private async Task PersistNewWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        WorkspaceItemViewModel ws,
        Vegha.Core.Domain.Environment env,
        string successMessage)
    {
        try
        {
            var dir = System.IO.Path.Combine(ws.FolderPath, Vegha.Core.FileFormat.WorkspaceModelLoader.EnvironmentsFolder);
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, SanitizeEnvFileName(env.Name) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix);
            await System.IO.File.WriteAllTextAsync(path,
                Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(
                    Vegha.Core.FileFormat.EnvironmentFile.FromDomain(env)));

            tab.Environments.Add(env);
            tab.SelectedEnvironment = env;

            var collections = App.Services.GetService<CollectionsViewModel>();
            if (collections is not null)
            {
                collections.GlobalEnvironments.Add(env);
                if (!collections.Environments.Contains(env)) collections.Environments.Add(env);
            }
            tab.StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    private async Task DeleteWorkspaceEnvAsync(
        Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel tab,
        Vegha.Core.Domain.Environment env,
        WorkspaceItemViewModel ws)
    {
        var confirm = new Vegha.App.Controls.Workspace.CloseWorkspaceDialog(env.Name, ws.FolderPath);
        confirm.Title = "Delete environment";
        confirm.SetPromptForEnvDelete();
        var ok = await confirm.ShowDialog<bool>(this);
        if (!ok) return;

        try
        {
            var envDir = System.IO.Path.Combine(ws.FolderPath, Vegha.Core.FileFormat.WorkspaceModelLoader.EnvironmentsFolder);
            var path = System.IO.Path.Combine(envDir, SanitizeEnvFileName(env.Name) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            tab.Environments.Remove(env);

            // Drop from the shared lists so the top-bar pill / env picker stop showing the
            // deleted env. Without this the env survived in CollectionsViewModel until the
            // user switched workspaces.
            var collections = App.Services.GetService<CollectionsViewModel>();
            if (collections is not null)
            {
                collections.CollectionEnvironments.Remove(env);
                collections.GlobalEnvironments.Remove(env);
                collections.Environments.Remove(env);
                if (ReferenceEquals(collections.ActiveEnvironment, env)) collections.ActiveEnvironment = null;
                if (ReferenceEquals(collections.ActiveGlobalEnvironment, env)) collections.ActiveGlobalEnvironment = null;
            }

            tab.StatusMessage = $"Deleted “{env.Name}”.";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>Gate that keeps checkpoint persistence from firing until the startup restore has
    /// finished. Without it, the collection/workspace activation that runs during startup would
    /// snapshot an empty tab set over the DB before <see cref="RestoreOpenTabsAsync"/> reads it.</summary>
    private bool _tabPersistenceReady;

    /// <summary>Snapshots every open tab's full state to the session DB. Cheap for the common
    /// case (only dirty/scratch tabs serialize a blob); safe to call on every checkpoint.</summary>
    private void PersistTabs(
        Vegha.App.ViewModels.Tabs.OpenTabsViewModel tabs,
        Vegha.Core.Persistence.TabStateStore store)
    {
        if (!_tabPersistenceReady) return;
        try
        {
            var rows = tabs.FullSnapshot()
                .Select(s => new Vegha.Core.Persistence.TabStateRow(
                    Id: s.Id,
                    WorkspaceId: s.WorkspaceId,
                    CollectionPath: s.CollectionPath,
                    SourcePath: s.SourcePath,
                    Name: s.Name,
                    Kind: s.Kind.ToString(),
                    OrderIndex: s.OrderIndex,
                    IsActive: s.IsActive,
                    IsDirty: s.IsDirty,
                    IsScratch: s.IsScratch,
                    StateBlob: s.StateBlob))
                .ToList();
            store.SaveAll(rows);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[tabs] persist failed: {ex}");
        }
    }

    private async Task ShowWelcomeAsync(AppSettings current)
    {
        var vm = App.Services.GetService<WelcomeViewModel>();
        if (vm is null) return;

        var dlg = new WelcomeDialog { DataContext = vm };
        var openImportAfter = false;
        var openFolderAfter = false;
        var openSampleAfter = false;
        vm.OnImport = () => openImportAfter = true;
        vm.OnOpenCollection = () => openFolderAfter = true;
        vm.OnTrySample = () => openSampleAfter = true;
        vm.OnDismiss = (dontShowAgain) =>
        {
            if (dontShowAgain && _appSettingsStore is not null)
                _appSettingsStore.Save(current with { WelcomeShown = true });
        };

        await dlg.ShowDialog(this);

        if (openImportAfter) await OpenImportWizardAsync();
        else if (openFolderAfter) await OpenCollectionFolderAsync();
        else if (openSampleAfter) OpenBundledSample();
    }

    /// <summary>Loads the bundled samples/petstore collection from the app directory. The
    /// folder ships next to the executable; we copy it to a per-user scratch location so
    /// the user can edit / delete safely without affecting the install.</summary>
    private void OpenBundledSample()
    {
        var collections = App.Services.GetService<CollectionsViewModel>();
        if (collections is null) return;

        // The bundled sample sits at <app-base>/samples/petstore. We copy it to
        // %LocalAppData%/Vegha/samples/petstore so user edits don't pollute
        // the install. If the copy already exists, just open it as-is.
        var src = Path.Combine(AppContext.BaseDirectory, "samples", "petstore");
        if (!Directory.Exists(src))
        {
            // Sample missing — surface via status, don't crash.
            collections.LoadFromDirectory(src);  // status will say "not found"
            return;
        }

        var dest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha", "samples", "petstore");
        if (!Directory.Exists(dest))
        {
            try { CopyDirectory(src, dest); }
            catch { /* fall through; loader will report */ }
        }
        collections.LoadFromDirectory(dest);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    // ---- File / Edit / View / Help menu handlers ----

    private void OnMenuNewRequest_Click(object? sender, RoutedEventArgs e) => CreateScratchRequest();

    private void OnMenuNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var collections = App.Services.GetService<CollectionsViewModel>();
        if (collections is not null) collections.StatusMessage = "Right-click a collection or folder to create a folder.";
    }

    private async void OnMenuOpenCollection_Click(object? sender, RoutedEventArgs e)
        => await OpenCollectionFolderAsync();

    private async void OnMenuImport_Click(object? sender, RoutedEventArgs e)
        => await OpenImportWizardAsync();

    private async void OnMenuSettings_Click(object? sender, RoutedEventArgs e)
        => await OpenSettingsDialogAsync();

    private void OnMenuExit_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnMenuSearchPalette_Click(object? sender, RoutedEventArgs e)
        => OpenSearchPalette();

    private void OpenSearchPalette()
    {
        var vm = App.Services.GetService<SearchPaletteViewModel>();
        if (vm is null) return;
        vm.Open();
        var win = new SearchPaletteWindow { DataContext = vm };
        win.Show(this);
    }

    private double _restoredCodegenWidth = 320;
    private const double CodegenMinWidth = 220;
    private bool _codegenCollapsed;

    /// <summary>Toggles the right-side codegen pane. Closing parks its current width so the
    /// next open restores the user's drag-resize. Collapses the splitter too so dragging
    /// it doesn't try to grow a hidden column.</summary>
    private void OnMenuToggleCodePanel_Click(object? sender, RoutedEventArgs e)
        => SetCodePanelCollapsed(!_codegenCollapsed);

    private void SetCodePanelCollapsed(bool collapse)
    {
        _codegenCollapsed = collapse;
        var col = MainContentGrid.ColumnDefinitions[5];
        var splitterCol = MainContentGrid.ColumnDefinitions[4];
        if (collapse)
        {
            // Snapshot current width so reopen restores the user's drag size.
            if (col.Width.IsAbsolute && col.Width.Value > 40)
                _restoredCodegenWidth = col.Width.Value;
            // MinWidth from XAML (220) prevents Width=0 from actually collapsing the column,
            // so override it here. Same for the 4px splitter column — collapsing both
            // gives the workspace the full width back.
            col.MinWidth = 0;
            col.Width = new GridLength(0, GridUnitType.Pixel);
            splitterCol.Width = new GridLength(0, GridUnitType.Pixel);
            CodegenContainer.IsVisible = false;
            RightPanelSplitter.IsVisible = false;
            ShowCodePanelMenu.Header = "Show code panel";
        }
        else
        {
            col.MinWidth = CodegenMinWidth;
            col.Width = new GridLength(_restoredCodegenWidth, GridUnitType.Pixel);
            splitterCol.Width = new GridLength(4, GridUnitType.Pixel);
            CodegenContainer.IsVisible = true;
            RightPanelSplitter.IsVisible = true;
            ShowCodePanelMenu.Header = "Hide code panel";
        }
        SaveCurrentLayout();
    }

    private void OnMenuDocs_Click(object? sender, RoutedEventArgs e)
        => HelpDialog.OpenUrl(HelpDialog.DocsUrl);

    private void OnMenuShortcuts_Click(object? sender, RoutedEventArgs e) => ShowHelp();

    private async void OnMenuAbout_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new AboutDialog();
        await dlg.ShowDialog(this);
    }

    /// <summary>Help → "Check for Updates…". Runs an interactive check whose phases surface in
    /// the update banner (checking → downloading → restart, or up-to-date / failed).</summary>
    private async void OnMenuCheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.Update.CheckNowCommand.ExecuteAsync(null);
    }

    /// <summary>Update banner → "Release notes": open the GitHub releases page in the browser.</summary>
    private void OnUpdateReleaseNotes_Click(object? sender, RoutedEventArgs e)
        => HelpDialog.OpenUrl(UpdateViewModel.ReleaseNotesUrl);

    /// <summary>If any open tabs are dirty, prompt the user before letting the window close.
    /// Cancel keeps the app running; Discard exits without saving; Save iterates dirty tabs and
    /// awaits each editor's SaveCommand, then closes once persistence settles.</summary>
    /// <summary>Sets the response-pane row height: persisted height when applicable, 0 when
    /// not (workspace + env editor tabs). Also collapses the splitter row in the same go so
    /// the workspace area visually claims the full vertical space.</summary>
    private void ApplyResponseRowHeight(bool applicable)
    {
        if (applicable)
        {
            // Restore the user's last-saved response-pane height (or default).
            var saved = _layoutStore?.Load().ResponsePaneHeight ?? LayoutSettings.Default.ResponsePaneHeight;
            WorkspaceGrid.RowDefinitions[2].MinHeight = 160;
            WorkspaceGrid.RowDefinitions[2].MaxHeight = 640;
            WorkspaceGrid.RowDefinitions[2].Height = new GridLength(saved, GridUnitType.Pixel);
            WorkspaceGrid.RowDefinitions[1].Height = new GridLength(4, GridUnitType.Pixel);
        }
        else
        {
            WorkspaceGrid.RowDefinitions[2].MinHeight = 0;
            WorkspaceGrid.RowDefinitions[2].Height = new GridLength(0, GridUnitType.Pixel);
            WorkspaceGrid.RowDefinitions[1].Height = new GridLength(0, GridUnitType.Pixel);
        }
    }

    /// <summary>Mitigates an AvaloniaEdit 11.1 shutdown bug where <c>LineNumberMargin.Render</c>
    /// throws <c>InvalidOperationException</c> on the final compositor commit (a stale
    /// <c>DocumentLine</c> reference is rendered after the document has been invalidated).
    /// Clearing <c>Window.Content</c> detaches every TextEditor from the visual tree before
    /// the framework's HandleClosed pass runs, so the failing render never happens.</summary>
    private void TearDownBeforeClose()
    {
        try { Content = null; }
        catch { /* best-effort — already mid-close */ }
    }

    private void OnClosing(object? sender, global::Avalonia.Controls.WindowClosingEventArgs e)
    {
        // No save/discard prompt: every tab's full state (including unsaved edits and scratch
        // drafts) is snapshotted to the session DB and reinstated on next launch. Just persist
        // and tear down.
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        var store = App.Services.GetService<Vegha.Core.Persistence.TabStateStore>();
        if (tabs is not null && store is not null) PersistTabs(tabs, store);
        TearDownBeforeClose();
    }

    private async Task OpenImportWizardAsync()
    {
        var vm = App.Services.GetService<ImportWizardViewModel>();
        if (vm is null) return;

        var collections = App.Services.GetService<CollectionsViewModel>();
        var workspaces = App.Services.GetService<WorkspacesViewModel>();

        // This entry point is the Import-collection flow. Environments are imported
        // from the Environments panel's own Import button — reject env files here with a
        // redirect message so the user isn't surprised by a silent env-only import.
        vm.AcceptEnvironments = false;

        // Destination is implicit now: every import lands under the active workspace's
        // collections/ folder (with collision suffix). The wizard no longer carries a
        // destination picker — that lived from before workspaces had a defined collections
        // subfolder.
        var collectionsRoot = workspaces?.ActiveWorkspace is { } active
            ? Path.Combine(active.FolderPath, "collections")
            : null;
        if (!string.IsNullOrEmpty(collectionsRoot))
        {
            Directory.CreateDirectory(collectionsRoot);
            vm.ActiveWorkspaceCollectionsRoot = collectionsRoot;
        }

        // We capture freshly-imported roots HERE (inside the OnCollectionConfirmed handler)
        // rather than diffing AvailableCollections after the wizard closes. The diff approach
        // was fragile — LoadFromDirectory has early-return branches (e.g. "path is already
        // under an existing root") that don't add a new entry, and there's no reliable way
        // to tell those apart from the success path by looking at AvailableCollections.
        var importedRoots = new List<CollectionRootViewModel>();
        vm.OnCollectionConfirmed = (c, stagedFolder) =>
        {
            if (string.IsNullOrEmpty(vm.ActiveWorkspaceCollectionsRoot)) return;

            string dest;
            // If the importer staged a folder (Bruno tree from disk / extracted zip / git
            // clone), copy the tree into the workspace's collections/ folder rather than
            // re-serializing the parsed Collection.
            if (!string.IsNullOrEmpty(stagedFolder) && Directory.Exists(stagedFolder))
            {
                dest = ResolveImportFolder(vm.ActiveWorkspaceCollectionsRoot, c.Name, stagedFolder);
                CopyImportTree(stagedFolder, dest);
            }
            else
            {
                // Otherwise serialize the parsed Collection out as a fresh Bruno tree.
                dest = ResolveImportFolder(vm.ActiveWorkspaceCollectionsRoot, c.Name, c.Name);
                Core.Importers.BruCollectionWriter.Write(dest, c);
            }
            collections?.LoadFromDirectory(dest);

            // Look up the newly-loaded root by destination path so the summary dialog can
            // operate on it. Falls back to the active collection (LoadFromDirectory
            // auto-activates new roots) if the path lookup misses for any reason.
            var newRoot = collections?.AvailableCollections.FirstOrDefault(r =>
                string.Equals(r.SourcePath, dest, StringComparison.OrdinalIgnoreCase));
            newRoot ??= collections?.ActiveCollection;
            if (newRoot is not null && !importedRoots.Contains(newRoot))
                importedRoots.Add(newRoot);
        };

        vm.OnEnvironmentConfirmed = env =>
        {
            // Postman environments default to the active collection's environments/ folder
            // so they live alongside the requests they're paired with. AddEnvironment writes
            // the file and updates the in-memory list; the watcher reload reconciles a
            // moment later (idempotent because the on-disk file matches the in-memory entry).
            collections?.AddEnvironment(env);
        };

        // Post-batch summary: surface "Imported X of Y collections" on the workspace
        // editor's Overview tab (the "Collection summary page" the user sees when a
        // WorkspaceTabViewModel happens to be open) and on the shared CollectionsViewModel
        // status channel so the status-bar toast picks it up too. The dedicated summary
        // dialog (opened below, after the wizard closes) is what the user actually sees in
        // the common case; this hook keeps the lightweight surfaces in sync for follow-up
        // glances.
        vm.OnBatchImported = (importedCount, totalRecognized) =>
        {
            if (importedCount <= 0) return;
            var message = totalRecognized > 0 && importedCount != totalRecognized
                ? $"Imported {importedCount} of {totalRecognized} collections."
                : $"Imported {importedCount} collection{(importedCount == 1 ? "" : "s")}.";
            if (collections is not null) collections.StatusMessage = message;
            var openTabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
            if (openTabs is null) return;
            foreach (var tab in openTabs.Tabs.OfType<Vegha.App.ViewModels.Tabs.WorkspaceTabViewModel>())
            {
                if (workspaces?.ActiveWorkspace is { } ws &&
                    !string.Equals(tab.WorkspaceItem.FolderPath, ws.FolderPath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                tab.OverviewMessage = message;
                tab.ActiveSection = "overview";
            }
        };

        var dlg = new ImportWizardDialog { DataContext = vm };
        await dlg.ShowDialog(this);

        // After the wizard returns, surface the post-import summary dialog showing just the
        // freshly-imported collections (collected directly inside OnCollectionConfirmed
        // above, not derived from a diff). Skipped silently when the user cancelled the
        // wizard or no collections landed.
        if (collections is null || workspaces is null || importedRoots.Count == 0) return;

        var summary = new ImportSummaryDialog(importedRoots, collections, workspaces);
        await summary.ShowDialog(this);
    }

    /// <summary>Recursively copies <paramref name="src"/> to <paramref name="dest"/>. Used
    /// by the import flow when the staged content is a folder (Bruno tree, extracted ZIP,
    /// git clone) — we copy rather than re-write so .bru files are byte-identical and any
    /// non-bru auxiliary files (README, docs/, etc.) carry over. .git/ is skipped so a
    /// freshly cloned repo doesn't drag its history into the workspace.</summary>
    private static void CopyImportTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + ".git", StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(dir.Replace(src, dest));
        }
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            File.Copy(file, file.Replace(src, dest), overwrite: true);
        }
    }

    /// <summary>Pick a directory under <paramref name="workspaceRoot"/> for an imported collection.
    /// Prefers the collection's own name (sanitized), falls back to the source filename without
    /// extension. If a directory by that name already exists, append " (2)", " (3)" etc. We never
    /// silently overwrite — the user wouldn't expect re-importing to clobber edits.</summary>
    private static string ResolveImportFolder(string workspaceRoot, string? collectionName, string sourcePath)
    {
        var baseName = !string.IsNullOrWhiteSpace(collectionName)
            ? Sanitize(collectionName!)
            : Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
        if (string.IsNullOrEmpty(baseName)) baseName = "imported";

        var candidate = Path.Combine(workspaceRoot, baseName);
        if (!Directory.Exists(candidate)) return candidate;

        for (var i = 2; i < 1000; i++)
        {
            var bumped = Path.Combine(workspaceRoot, $"{baseName} ({i})");
            if (!Directory.Exists(bumped)) return bumped;
        }
        // Pathological fallback — 1000 collisions is silly, but let's not throw.
        return Path.Combine(workspaceRoot, baseName + "-" + DateTime.UtcNow.Ticks);

        static string Sanitize(string n)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(n.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        }
    }

    /// <summary>Opens the modal New Request dialog and, on confirm, persists the request via
    /// <see cref="CollectionsViewModel.CreateRequestFromDialog"/>. The new file is opened in
    /// a tab automatically so the user can start editing it.</summary>
    private async Task OpenNewRequestDialogAsync(CollectionsViewModel collections, Vegha.App.ViewModels.CollectionNodeViewModel node)
    {
        var dlg = new Vegha.App.Controls.Workspace.NewRequestDialog();
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is null) return;

        var r = dlg.Result;
        var newPath = collections.CreateRequestFromDialog(
            node, r.Kind, r.Name, r.Method, r.Url, r.CurlCommand);
        if (string.IsNullOrEmpty(newPath)) return;

        // Open the freshly created request in a new tab. ParseBruFromDiskAsync gives us a
        // RequestItem; OpenTabsViewModel + the existing tab strip handle the rest. Stamp the
        // owning collection's root path so the tab is filtered to that collection's scope —
        // without it the new request leaked into every collection's tab strip.
        try
        {
            var item = await ParseBruFromDiskAsync(newPath);
            var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
            var collectionPath = collections.ResolveCollectionRootPath(node);
            if (item is not null && tabs is not null)
                tabs.OpenOrActivate(item, newPath, collectionPath: collectionPath);
        }
        catch { /* best-effort — the file is on disk regardless. */ }
    }

    // ===================== Scratch ("+") request tab actions =====================

    /// <summary>Creates a fresh "Untitled" scratch request in the active workspace and opens it.
    /// Used by the tab strip's "+" button, the File ▸ New Request menu, the welcome card, and the
    /// tab menu's "New Request" entry. Scratch tabs live only in the session DB (no collection
    /// file) until promoted via "Save to collection…".</summary>
    private void CreateScratchRequest()
    {
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (tabs is null) return;
        var workspaceId = App.Services.GetService<WorkspacesViewModel>()?.ActiveWorkspace?.FolderPath;
        tabs.CreateScratch(workspaceId);
    }

    /// <summary>Duplicates a tab's current (possibly unsaved) state into a new scratch request.</summary>
    private void CloneTabToScratch(Vegha.App.ViewModels.Tabs.RequestTabViewModel tab)
    {
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (tabs is null) return;
        if (tab is not Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel http)
            return; // SOAP (and any future non-editor) tabs aren't clonable through this path yet.

        var workspaceId = App.Services.GetService<WorkspacesViewModel>()?.ActiveWorkspace?.FolderPath;
        var clone = tabs.CreateScratch(workspaceId);
        // Copy the source's current editor state into the new draft and mark it unsaved.
        var item = http.Editor.BuildRequestItemFromVm();
        clone.Editor.LoadFromRequestItem(item, sourcePath: null);
        clone.Editor.IsDirty = true;
    }

    /// <summary>Renames a tab's backing file (scratch or collection) and re-keys the open tab in
    /// place, preserving the editor's current edits. Drafts with no file just get a new title.</summary>
    private async Task RenameTabAsync(Vegha.App.ViewModels.Tabs.RequestTabViewModel tab)
    {
        var dlg = new Vegha.App.Controls.Workspace.RenameDialog("Rename request", "Request name", tab.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrWhiteSpace(dlg.ResultName)) return;
        var newName = dlg.ResultName.Trim();
        if (string.Equals(newName, tab.Name, StringComparison.Ordinal)) return;

        // Draft with no backing file: in-memory title change is all we can do.
        if (string.IsNullOrEmpty(tab.SourcePath) || tab is not Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel http)
        {
            tab.Name = newName;
            return;
        }

        try
        {
            var oldPath = tab.SourcePath!;
            var dir = Path.GetDirectoryName(oldPath);
            var ext = Path.GetExtension(oldPath);
            if (string.IsNullOrEmpty(dir)) { tab.Name = newName; return; }

            var stem = SanitizeFileStem(newName);
            var newPath = Path.Combine(dir, stem + ext);
            if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            {
                var collisions = App.Services.GetService<CollectionsViewModel>();
                if (collisions is not null) collisions.StatusMessage = $"“{stem}{ext}” already exists.";
                return;
            }

            // Re-emit with the new meta.name (keeps the tree/label in sync) and persist the
            // editor's current state, then move the tab onto the new path.
            var item = http.Editor.BuildRequestItemFromVm() with { Name = stem };
            File.WriteAllText(newPath, Vegha.Core.Importers.BruEmitter.Emit(item));
            if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                File.Delete(oldPath);

            tab.Id = newPath;
            tab.SourcePath = newPath;
            tab.Name = stem;
            http.Editor.SourcePath = newPath;
            http.Editor.IsDirty = false;
            // Collection-backed tabs refresh themselves: the per-root file watcher picks up the
            // move (delete old + write new) and reloads the tree so the renamed file shows.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[rename] failed: {ex}");
        }
    }

    private static string SanitizeFileStem(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>Discards a tab's unsaved edits by reloading it from disk.</summary>
    private async Task RevertTabAsync(Vegha.App.ViewModels.Tabs.RequestTabViewModel tab)
    {
        if (tab is not Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel http || string.IsNullOrEmpty(tab.SourcePath))
            return;
        try
        {
            var item = await ParseBruFromDiskAsync(tab.SourcePath!);
            if (item is null) return;
            http.Editor.LoadFromRequestItem(item, tab.SourcePath);
            http.Editor.IsDirty = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[revert] failed: {ex}");
        }
    }

    /// <summary>Promotes a scratch request into the active collection: the user picks the target
    /// folder within it (or creates a new folder), then the full request is written there, removed
    /// from scratch, and re-opened as a normal collection-scoped tab.</summary>
    private async Task SaveTabToCollectionAsync(Vegha.App.ViewModels.Tabs.RequestTabViewModel tab)
    {
        var collections = App.Services.GetService<CollectionsViewModel>();
        var tabs = App.Services.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
        if (collections is null || tabs is null) return;
        if (tab is not Vegha.App.ViewModels.Tabs.HttpRequestTabViewModel http) return;

        // Scope the picker to the CURRENT collection only — folders (and new-folder creation) are
        // confined to it rather than spanning every collection in the workspace.
        var activeCollection = collections.ActiveCollection;
        if (activeCollection is null)
        {
            collections.StatusMessage = "Open or select a collection first.";
            return;
        }

        var dlg = new Vegha.App.Controls.Workspace.SaveToCollectionDialog(activeCollection, tab.Name);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is null) return;
        var result = dlg.Result;

        try
        {
            var item = http.Editor.BuildRequestItemFromVm();
            var newPath = collections.CreateRequestFromItemInDirectory(result.DirectoryPath, item, result.Name);
            if (string.IsNullOrEmpty(newPath)) return;

            // Saving from a history-replay tab happens while the History sidebar section is
            // active, where the freshly-opened (non-history) request tab would be hidden. Flip
            // back to Collections so the user lands on their newly-saved request.
            if (tab is Vegha.App.ViewModels.Tabs.HistoryTabViewModel
                && DataContext is MainWindowViewModel mwvm)
            {
                mwvm.ActiveSidebarSection = "collections";
            }

            // Close the scratch tab (drops it from memory + the session DB at the next snapshot),
            // then open the freshly-saved request, scoped to the collection it now lives in.
            tabs.CloseTab(tab);
            var saved = await ParseBruFromDiskAsync(newPath);
            if (saved is not null)
            {
                var root = collections.FindRootForDirectory(result.DirectoryPath);
                tabs.OpenOrActivate(saved, newPath, root?.Collection,
                    Array.Empty<Vegha.Core.Domain.Folder>(), root?.SourcePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[promote] failed: {ex}");
        }
    }

    private async Task OpenSettingsDialogAsync()
    {
        var vm = App.Services.GetService<Vegha.App.ViewModels.Settings.SettingsWindowViewModel>();
        if (vm is null) return;
        var dlg = new Vegha.App.Controls.Settings.SettingsWindow
        {
            DataContext = vm,
        };
        await dlg.ShowDialog(this);
        // AppSettingsStore.Changed (subscribed in OnLoaded) already pushes the live updates;
        // nothing further to do here whether the user saved or cancelled.

        // The Secret Manager page persists provider configs immediately (outside Save), so
        // re-sync the registry once the window closes — adds/removes there take effect now.
        ReloadSecretProviders();
    }

    /// <summary>Rebuilds the secret-provider registry from the persisted, encrypted configs.
    /// Best-effort: a failure here must not block the UI.</summary>
    private static void ReloadSecretProviders()
    {
        try
        {
            var registry = App.Services.GetService<Vegha.Integrations.Secrets.SecretRegistry>();
            if (registry is not null)
                Vegha.App.Secrets.SecretProviderRegistrar.Reload(registry);
        }
        catch
        {
            /* tolerate — secret resolution simply stays unconfigured */
        }
    }

    /// <summary>Re-applies the entire AppSettings record across all consumers. Called once
    /// at startup and again whenever <see cref="AppSettingsStore.Changed"/> fires.</summary>
    private void ApplyAllAppSettings(AppSettings s)
    {
        // Theme — mode + named variant.
        var themeService = App.Services?.GetService<Vegha.App.Services.ThemeService>();
        if (themeService is not null)
        {
            themeService.ApplyMode(string.IsNullOrEmpty(s.ThemeMode) ? s.Theme : s.ThemeMode);
            themeService.ApplyVariantForMode(
                string.IsNullOrEmpty(s.ThemeMode) ? s.Theme : s.ThemeMode,
                s.ThemeVariantLight,
                s.ThemeVariantDark);
        }
        else
        {
            ApplyTheme(s.Theme);
        }

        // Fonts + editor prefs pushed into Application.Resources so editors update live.
        ApplyFontSize(s.FontSize);
        ApplyEditorResources(s);

        // Interface zoom — push to the shared ZoomHost which actually drives the
        // LayoutTransform on every attached window (main window + dialogs).
        ZoomHost.SetZoom(Math.Clamp(s.InterfaceZoom, 0.8, 2.0));
        if (DataContext is MainWindowViewModel mwvm)
            mwvm.InterfaceZoom = Math.Clamp(s.InterfaceZoom, 0.8, 2.0);

        // HTTP executor: proxy, SSL session caching, request timeout, max body size, custom CAs.
        var executor = App.Services?.GetService<Vegha.Core.Requests.HttpExecutor>();
        if (executor is not null)
        {
            executor.UpdateTrustedCAs(Vegha.Core.Requests.CertificateLoader.Parse(s.CustomTrustCAs));
            executor.UpdateProxy(Vegha.Core.Persistence.ProxyResolver.Build(s));
            executor.UpdateCacheSslSessions(s.CacheSslSessions);
            executor.UpdateTimeout(TimeSpan.FromSeconds(Math.Max(1, s.RequestTimeoutSeconds)));
            executor.UpdateMaxBodyBytes((long)Math.Max(1, s.MaxBodySizeMb) * 1024 * 1024);
        }

        // History store: persistence enable + preview cap + retention policy.
        var history = App.Services?.GetService<Vegha.Core.History.HistoryStore>();
        if (history is not null)
        {
            history.Enabled = s.SaveResponsesToHistory;
            // PreviewMaxChars caps how much of the response we persist as the body preview
            // column. Multiply by 1024 (rough chars per KB for UTF-8 prose) and clamp to a
            // reasonable upper bound so a 2 GB body-cap doesn't try to write a 2 GB row.
            history.MaxPreviewChars = Math.Min(int.MaxValue, Math.Max(1, s.MaxBodySizeMb) * 1024 * 1024);
            history.MaxRetained = Math.Max(1, s.HistoryRetentionMaxEntries);
            history.MaxAge = s.HistoryRetentionDays > 0
                ? TimeSpan.FromDays(s.HistoryRetentionDays)
                : TimeSpan.Zero;
        }

    }

    private void ApplyEditorResources(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Resources["EditorTabSize"] = Math.Clamp(s.EditorTabSize, 1, 8);
        app.Resources["EditorWordWrap"] = s.EditorWordWrap;
        app.Resources["EditorShowLineNumbers"] = s.EditorShowLineNumbers;
    }

    private async Task OpenCollectionFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new global::Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Open a Bruno collection folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var collections = App.Services.GetService<CollectionsViewModel>();
        collections?.LoadFromDirectory(path);
    }

    private void ApplyTheme(string theme)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" => global::Avalonia.Styling.ThemeVariant.Light,
            "dark" => global::Avalonia.Styling.ThemeVariant.Dark,
            _ => global::Avalonia.Styling.ThemeVariant.Default,
        };
    }

    /// <summary>Pushes the user's preferred font size into the Application resource dict so
    /// every code editor that binds <c>FontSize="{DynamicResource EditorFontSize}"</c> (request
    /// body + response body) picks it up live. Chrome / labels / menus stay at the fixed
    /// <c>AppFontSize</c> — they aren't touched here on purpose, since the user wants the
    /// rest of the app to keep a consistent size regardless of this setting.</summary>
    private void ApplyFontSize(int size)
    {
        var clamped = Math.Clamp(size, 8, 24);
        var app = Application.Current;
        if (app is not null) app.Resources["EditorFontSize"] = (double)clamped;
    }

    /// <summary>The hamburger-menu items carry Windows-style InputGestures from XAML. On macOS
    /// swap them to ⌘-based gestures so the menu shows ⌘T / ⌘, instead of Ctrl+T / Ctrl+,.
    /// Display only — the functional bindings live in Window.KeyBindings and the Ctrl/Cmd+K
    /// tunnel handler. Windows and Linux keep the Ctrl gestures authored in XAML.</summary>
    private void ApplyMenuShortcutGestures()
    {
        if (!ShortcutFormatter.IsMac)
            return;

        MenuNewRequest.InputGesture = KeyGesture.Parse("Cmd+T");
        MenuOpenCollection.InputGesture = KeyGesture.Parse("Cmd+O");
        MenuImport.InputGesture = KeyGesture.Parse("Cmd+I");
        MenuSettings.InputGesture = KeyGesture.Parse("Cmd+,");
        MenuFindRequest.InputGesture = KeyGesture.Parse("Cmd+K");
    }

    private void ShowHelp()
    {
        var dlg = new HelpDialog { FormatGesture = ShortcutFormatter.Format };
        dlg.ShowDialog(this);
    }

    private void ApplyLayout(LayoutSettings settings)
    {
        MainContentGrid.ColumnDefinitions[1].Width = new GridLength(settings.SidebarWidth, GridUnitType.Pixel);
        // The codegen column is initialized to the persisted width either way — if the panel
        // is collapsed, SetCodePanelCollapsed(true) below overrides it to 0; if expanded, the
        // user's last drag-resize value is what's shown.
        MainContentGrid.ColumnDefinitions[5].Width = new GridLength(settings.RightPanelWidth, GridUnitType.Pixel);
        _restoredCodegenWidth = settings.RightPanelWidth;
        WorkspaceGrid.RowDefinitions[2].Height = new GridLength(settings.ResponsePaneHeight, GridUnitType.Pixel);
        // Apply codegen collapsed state. Defaults to true (closed) so first-launch users
        // see the workspace at full width.
        SetCodePanelCollapsed(settings.IsCodegenCollapsed);
    }

    private void SaveCurrentLayout()
    {
        if (_layoutStore is null) return;
        // When the panel is collapsed, columns[5].Width is 0; persist the user's last open
        // width from _restoredCodegenWidth instead so reopening restores the right size.
        var rightWidth = _codegenCollapsed
            ? _restoredCodegenWidth
            : MainContentGrid.ColumnDefinitions[5].Width.Value;
        _layoutStore.Save(new LayoutSettings(
            SidebarWidth: MainContentGrid.ColumnDefinitions[1].Width.Value,
            RightPanelWidth: rightWidth,
            ResponsePaneHeight: WorkspaceGrid.RowDefinitions[2].Height.Value)
        {
            IsCodegenCollapsed = _codegenCollapsed,
        });
    }

    // --- Splitter handlers ---

    private void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e) => SaveCurrentLayout();

    private void OnRightPanelSplitterDragCompleted(object? sender, VectorEventArgs e) => SaveCurrentLayout();

    private void OnResponseSplitterDragCompleted(object? sender, VectorEventArgs e) => SaveCurrentLayout();

    private void OnSidebarSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        MainContentGrid.ColumnDefinitions[1].Width = new GridLength(LayoutSettings.Default.SidebarWidth, GridUnitType.Pixel);
        SaveCurrentLayout();
        e.Handled = true;
    }

    private void OnRightPanelSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        MainContentGrid.ColumnDefinitions[5].Width = new GridLength(LayoutSettings.Default.RightPanelWidth, GridUnitType.Pixel);
        SaveCurrentLayout();
        e.Handled = true;
    }

    private void OnResponseSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        WorkspaceGrid.RowDefinitions[2].Height = new GridLength(LayoutSettings.Default.ResponsePaneHeight, GridUnitType.Pixel);
        SaveCurrentLayout();
        e.Handled = true;
    }

    /// <summary>Opens (or focuses) a diff tab for the given change row. The mode is picked
    /// from the row's staged-state: staged rows show HEAD↔Index, unstaged rows show
    /// HEAD↔WorkingTree, conflicted rows open in single-pane merge mode.</summary>
    private void OpenGitDiffTab(GitViewModel git, ViewModels.GitChangeRow row, ViewModels.Tabs.OpenTabsViewModel tabs)
    {
        var repoPath = git.RepoPath;
        if (string.IsNullOrEmpty(repoPath)) return;
        var gitService = App.Services.GetService<Vegha.Integrations.Git.GitService>();
        if (gitService is null) return;

        var mode = row.Kind switch
        {
            Vegha.Integrations.Git.GitChangeKind.Conflict => ViewModels.Tabs.DiffMode.Merge,
            _ => row.IsStaged ? ViewModels.Tabs.DiffMode.IndexVsHead : ViewModels.Tabs.DiffMode.WorkingTreeVsHead,
        };

        // De-dup: if a tab is already open for the same (file, mode), focus it instead of
        // pushing a new instance.
        var tabId = $"diff:{mode}:{row.Path}";
        var existing = tabs.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (existing is not null)
        {
            tabs.ActiveTab = existing;
            return;
        }

        var diffTab = new ViewModels.Tabs.GitDiffTabViewModel(
            gitService, repoPath, row.Path, mode,
            collectionPath: repoPath);
        tabs.Tabs.Add(diffTab);
        tabs.ActiveTab = diffTab;
    }

    /// <summary>Opens the working-tree file in the system default app.</summary>
    private void OpenFileExternally(GitViewModel git, ViewModels.GitChangeRow row)
    {
        if (string.IsNullOrEmpty(git.RepoPath)) return;
        try
        {
            var abs = System.IO.Path.Combine(git.RepoPath, row.Path);
            if (!System.IO.File.Exists(abs)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(abs) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Prompts for git user.name / user.email and writes the values to local config.
    /// Triggered from the GitPanel's "Set git identity" inline warning.</summary>
    private async Task ShowGitIdentityDialogAsync(GitViewModel git)
    {
        var dialog = new Controls.Workspace.GitIdentityDialog(git.Identity.Name ?? string.Empty, git.Identity.Email ?? string.Empty);
        var ok = await dialog.ShowDialog<bool>(this);
        if (!ok) return;
        if (string.IsNullOrWhiteSpace(dialog.UserName) || string.IsNullOrWhiteSpace(dialog.UserEmail)) return;
        await git.SetGitIdentityAsync((dialog.UserName!, dialog.UserEmail!));
    }
}
