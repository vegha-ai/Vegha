using Vegha.App.Services;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Settings;
using Vegha.Core.Persistence;
using Vegha.Core.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vegha.App;

internal static class ServiceRegistration
{
    public static IServiceCollection AddVeghaServices(this IServiceCollection services)
    {
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton<LayoutSettingsStore>();
        services.AddSingleton<AppSettingsStore>();

        // Theme switcher (mode + named variant). Touches Application.Current.Resources, so a
        // singleton is correct — one swap target.
        services.AddSingleton<ThemeService>();

        // Settings dialog VM — transient so each open gets a fresh in-memory snapshot
        // independent of the persisted state until Save commits.
        services.AddTransient<SettingsWindowViewModel>(sp => new SettingsWindowViewModel(
            store: sp.GetRequiredService<AppSettingsStore>(),
            shortcutRows: KeyboardShortcutsCatalog.All,
            secretProviderFactory: Vegha.App.Secrets.SecretProviderRegistrar.TryCreate));

        services.AddTransient<WelcomeViewModel>();
        services.AddSingleton<GrpcWorkspaceViewModel>();
        services.AddHttpClient();
        services.AddSingleton<CookieJarStore>(_ =>
        {
            var store = new CookieJarStore();
            // Fire-and-forget so DI build stays off the SQLite read. HttpExecutor awaits
            // store.ReadyAsync before reading the jar — worst case the first request waits.
            store.BeginInitialLoad();
            return store;
        });
        services.AddSingleton<HttpExecutor>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var cookies = sp.GetRequiredService<CookieJarStore>();
            // Pull custom CAs from AppSettings so corporate roots validate without the user
            // disabling SSL globally. Failures parsing individual entries are tolerated.
            var settings = sp.GetService<Vegha.Core.Persistence.AppSettingsStore>()?.Load();
            var trusted = Vegha.Core.Requests.CertificateLoader.Parse(settings?.CustomTrustCAs);
            return new HttpExecutor(factory.CreateClient("default"), cookies, trusted);
        });
        services.AddSingleton<Vegha.Core.Scripting.IBruRequestExecutor>(sp =>
            new BruExecutorAdapter(sp.GetRequiredService<HttpExecutor>()));
        services.AddSingleton<Vegha.Core.Scripting.IBruCookieJar>(sp =>
            new Vegha.Core.Requests.CookieJarBruAdapter(sp.GetRequiredService<CookieJarStore>()));
        services.AddSingleton<Vegha.Core.Scripting.JintHost>(sp =>
            new Vegha.Core.Scripting.JintHost(
                subRequestExecutor: sp.GetRequiredService<Vegha.Core.Scripting.IBruRequestExecutor>(),
                cookieJar: sp.GetRequiredService<Vegha.Core.Scripting.IBruCookieJar>()));
        services.AddSingleton<OAuth2TokenAcquirer>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new OAuth2TokenAcquirer(factory.CreateClient("oauth2"));
        });
        services.AddSingleton<Vegha.Core.History.HistoryStore>(_ => new Vegha.Core.History.HistoryStore());
        services.AddSingleton<Vegha.Core.Persistence.WorkspaceStore>();
        services.AddSingleton<Vegha.Core.Persistence.TabSessionStore>(_ => new Vegha.Core.Persistence.TabSessionStore());
        services.AddSingleton<Vegha.Core.Persistence.RecentItemsStore>(_ => new Vegha.Core.Persistence.RecentItemsStore());
        services.AddSingleton<Vegha.Core.Persistence.OpenApiLinkStore>(_ => new Vegha.Core.Persistence.OpenApiLinkStore());
        services.AddSingleton<Vegha.Core.Persistence.SecretProviderConfigStore>(_ => new Vegha.Core.Persistence.SecretProviderConfigStore());

        // Per-tab editor: transient. The OpenTabsViewModel asks for a fresh editor
        // for every tab the user opens.
        services.AddTransient<RequestEditorViewModel>();
        services.AddSingleton<Func<RequestEditorViewModel>>(sp =>
            () => sp.GetRequiredService<RequestEditorViewModel>());

        // SOAP workspace VM is also per-tab so each SOAP request keeps its own
        // WSDL + operation + body state.
        services.AddTransient<SoapWorkspaceViewModel>();
        services.AddSingleton<Func<SoapWorkspaceViewModel>>(sp =>
            () => sp.GetRequiredService<SoapWorkspaceViewModel>());

        services.AddSingleton<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>(sp =>
            new Vegha.App.ViewModels.Tabs.OpenTabsViewModel(
                editorFactory: sp.GetRequiredService<Func<RequestEditorViewModel>>(),
                soapFactory: sp.GetRequiredService<Func<SoapWorkspaceViewModel>>(),
                logger: sp.GetRequiredService<ILogger<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>>()));

        services.AddSingleton<CollectionsViewModel>(sp =>
        {
            var collections = new CollectionsViewModel(
                sp.GetRequiredService<RequestEditorViewModel>(),
                sp.GetRequiredService<ILogger<CollectionsViewModel>>(),
                sp.GetRequiredService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>(),
                sp.GetRequiredService<HttpExecutor>(),
                sp.GetRequiredService<Vegha.Core.Persistence.RecentItemsStore>());
            // Let OpenTabs read the current env snapshot at tab-build time so drafts /
            // dialog-created / restored tabs show resolved {{var}} immediately, not on
            // the next env switch.
            var openTabs = sp.GetRequiredService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>();
            // Merged env snapshot: workspace (global) env underneath the collection env, so the
            // collection env wins on a name collision.
            openTabs.EnvironmentSnapshotProvider = () =>
                CollectionsViewModel.SnapshotMerged(collections.ActiveGlobalEnvironment, collections.ActiveEnvironment);
            openTabs.SecretNamesProvider = () =>
                CollectionsViewModel.SnapshotMergedSecretNames(collections.ActiveGlobalEnvironment, collections.ActiveEnvironment);
            // Tabs ask for the current workspace-level inheritance payload on build so the
            // {{var}} + script merge always sees the latest workspace state. The workspace
            // *environment* is NOT carried here — it is applied through the environment layer
            // (SnapshotMerged) below the collection environment. Feeding it into the workspace
            // inheritance Variables would mis-rank it above the collection environment.
            openTabs.WorkspaceContextProvider = () =>
            {
                return new Vegha.Core.Requests.RequestComposition.WorkspaceContext(
                    Variables: null,
                    PreRequestScript: collections.WorkspacePreRequestScript,
                    PostResponseScript: collections.WorkspacePostResponseScript,
                    TestsScript: collections.WorkspaceTestsScript);
            };

            // Link the tab strip's filter scope to the active collection. Without this the
            // VisibleTabs view never narrows when the user switches collections.
            collections.ActiveCollectionChanged += (_, e) =>
                openTabs.ActiveScope = e.NewCollectionPath;
            // Initial sync — workspace bootstrap selects an active collection before this
            // wiring runs, so push the current value through once.
            openTabs.ActiveScope = collections.ActiveCollection?.SourcePath;
            return collections;
        });
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<CookiesViewModel>();
        services.AddSingleton<Vegha.Integrations.Git.GitProcessRunner>();
        services.AddSingleton<Vegha.Integrations.Git.GitCredentialsService>();
        services.AddSingleton<Vegha.Integrations.Git.GitService>();
        services.AddSingleton<GitViewModel>();
        services.AddSingleton<Vegha.Integrations.Secrets.SecretRegistry>();
        services.AddSingleton<OpenApiViewModel>(sp => new OpenApiViewModel(
            sp.GetRequiredService<CollectionsViewModel>(),
            sp.GetRequiredService<ILogger<OpenApiViewModel>>(),
            sp.GetService<WorkspacesViewModel>()));
        services.AddSingleton<Vegha.App.ViewModels.Runner.RunnerSidebarViewModel>(sp =>
            new Vegha.App.ViewModels.Runner.RunnerSidebarViewModel(
                sp.GetRequiredService<CollectionsViewModel>()));
        services.AddSingleton<EnvironmentsViewModel>();
        services.AddSingleton<WebSocketViewModel>();
        services.AddTransient<ImportWizardViewModel>();
        services.AddSingleton<CodegenViewModel>(sp => new CodegenViewModel(
            sp.GetRequiredService<RequestEditorViewModel>(),
            sp.GetService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>()));
        services.AddSingleton<WorkspacesViewModel>();
        services.AddSingleton<SearchPaletteViewModel>(sp => new SearchPaletteViewModel(
            sp.GetRequiredService<Vegha.App.ViewModels.Tabs.OpenTabsViewModel>(),
            sp.GetRequiredService<CollectionsViewModel>(),
            sp.GetService<Vegha.Core.Persistence.RecentItemsStore>()));
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
