using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Open request tabs. Replaces the previous singleton editor — every open
    /// request gets its own ViewModel and the workspace area renders the active tab's
    /// content based on its kind.</summary>
    public OpenTabsViewModel OpenTabs { get; }

    /// <summary>Convenience: the active tab's editor when it's an HTTP/GraphQL kind.
    /// Codegen + response display bind to this. Returns a "seed" editor when no tab is
    /// open so existing controls don't crash on null bindings.</summary>
    public RequestEditorViewModel RequestEditor =>
        (OpenTabs.ActiveTab as HttpRequestTabViewModel)?.Editor ?? _seedEditor;

    private readonly RequestEditorViewModel _seedEditor;

    // Painted-immediately VMs: eager (resolved at MainWindowViewModel construction).
    public CollectionsViewModel Collections { get; }
    public CodegenViewModel Codegen { get; }
    public WorkspacesViewModel Workspaces { get; }

    // Non-paint VMs: lazy — only resolved when the user clicks the corresponding activity
    // rail tab (or any other binding touches the property). DI singleton ctors that touch
    // filesystem / SQLite / git repo state stay off the cold-startup critical path.
    private readonly Lazy<HistoryViewModel> _history;
    private readonly Lazy<CookiesViewModel> _cookies;
    private readonly Lazy<GitViewModel> _git;
    private readonly Lazy<OpenApiViewModel> _openApi;
    private readonly Lazy<Runner.RunnerSidebarViewModel> _runner;
    private readonly Lazy<EnvironmentsViewModel> _environmentsEditor;
    private readonly Lazy<WebSocketViewModel?> _webSocket;
    private readonly Lazy<GrpcWorkspaceViewModel?> _grpc;

    public HistoryViewModel History => _history.Value;
    public CookiesViewModel Cookies => _cookies.Value;
    public GitViewModel Git => _git.Value;
    public OpenApiViewModel OpenApi => _openApi.Value;
    public Runner.RunnerSidebarViewModel Runner => _runner.Value;
    public EnvironmentsViewModel EnvironmentsEditor => _environmentsEditor.Value;
    public WebSocketViewModel? WebSocket => _webSocket.Value;
    public GrpcWorkspaceViewModel? Grpc => _grpc.Value;

    /// <summary>Which sidebar panel is active (matches the activity rail selection).</summary>
    [ObservableProperty]
    private string _activeSidebarSection = "collections";

    /// <summary>Application-wide UI zoom factor. Bound to a ScaleTransform on the root
    /// LayoutTransformControl in MainWindow.axaml — changes propagate live.</summary>
    [ObservableProperty]
    private double _interfaceZoom = 1.0;

    private AppSettingsStore? _settingsStore;

    /// <summary>Called once by MainWindow after construction. We delay this rather than
    /// inject the store via the ctor to avoid threading every test fixture with one.</summary>
    public void AttachSettingsStore(AppSettingsStore store)
    {
        _settingsStore = store;
        InterfaceZoom = Math.Clamp(store.Load().InterfaceZoom, 0.8, 2.0);
    }

    [RelayCommand]
    private void IncreaseZoom() => SetZoom(InterfaceZoom + 0.1);

    [RelayCommand]
    private void DecreaseZoom() => SetZoom(InterfaceZoom - 0.1);

    [RelayCommand]
    private void ResetZoom() => SetZoom(1.0);

    private void SetZoom(double v)
    {
        InterfaceZoom = Math.Round(Math.Clamp(v, 0.8, 2.0), 2);
        if (_settingsStore is not null)
        {
            var s = _settingsStore.Load() with { InterfaceZoom = InterfaceZoom };
            _settingsStore.Save(s);
        }
    }

    /// <summary>Window chrome title (shown in OS taskbar / Alt+Tab). The custom title bar
    /// renders the brand explicitly; the workspace name is no longer suffixed here per
    /// design — the workspace dropdown in the title bar shows that information.</summary>
    public string Title => "Vegha";

    public MainWindowViewModel(
        IServiceProvider services,
        RequestEditorViewModel seedEditor,
        OpenTabsViewModel openTabs,
        CollectionsViewModel collections,
        CodegenViewModel codegen,
        WorkspacesViewModel workspaces)
    {
        _seedEditor = seedEditor;
        OpenTabs = openTabs;
        Collections = collections;
        Codegen = codegen;
        Workspaces = workspaces;

        _history = new Lazy<HistoryViewModel>(() => (HistoryViewModel)services.GetService(typeof(HistoryViewModel))!);
        _cookies = new Lazy<CookiesViewModel>(() => (CookiesViewModel)services.GetService(typeof(CookiesViewModel))!);
        _git = new Lazy<GitViewModel>(() => (GitViewModel)services.GetService(typeof(GitViewModel))!);
        _openApi = new Lazy<OpenApiViewModel>(() => (OpenApiViewModel)services.GetService(typeof(OpenApiViewModel))!);
        _runner = new Lazy<Runner.RunnerSidebarViewModel>(() => (Runner.RunnerSidebarViewModel)services.GetService(typeof(Runner.RunnerSidebarViewModel))!);
        _environmentsEditor = new Lazy<EnvironmentsViewModel>(() => (EnvironmentsViewModel)services.GetService(typeof(EnvironmentsViewModel))!);
        _webSocket = new Lazy<WebSocketViewModel?>(() => services.GetService(typeof(WebSocketViewModel)) as WebSocketViewModel);
        _grpc = new Lazy<GrpcWorkspaceViewModel?>(() => services.GetService(typeof(GrpcWorkspaceViewModel)) as GrpcWorkspaceViewModel);

        Workspaces.PropertyChanged += OnWorkspacesPropertyChanged;
        OpenTabs.PropertyChanged += OnOpenTabsPropertyChanged;
    }

    private void OnOpenTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenTabsViewModel.ActiveTab))
            OnPropertyChanged(nameof(RequestEditor));
    }

    private void OnWorkspacesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspacesViewModel.ActiveWorkspace))
            OnPropertyChanged(nameof(Title));
    }

    partial void OnActiveSidebarSectionChanged(string value)
    {
        if (value == "history") _ = History.RefreshAsync();
        if (value == "git") Git.Refresh();
        if (value == "runner") _ = Runner;  // touch lazy to instantiate
        if (value == "environments") EnvironmentsEditor.Refresh();
        // Flip the tab-strip kind filter so diff tabs only show in git mode, history tabs
        // only show in history mode, run tabs only show in runner mode, and request tabs
        // are hidden in any of those. Done last so the panel's own Refresh has already
        // populated state.
        OpenTabs.IsGitMode = value == "git";
        OpenTabs.IsHistoryMode = value == "history";
        OpenTabs.IsRunnerMode = value == "runner";
    }
}
