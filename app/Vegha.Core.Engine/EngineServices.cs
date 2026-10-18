using Vegha.Core.History;
using Vegha.Core.Persistence;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Vegha.Integrations.Secrets;

namespace Vegha.Core.Engine;

/// <summary>How a non-desktop host (CLI, VS Code sidecar, tests) wants its services built.</summary>
public sealed record EngineOptions
{
    /// <summary>Tag written to every history row (<c>vscode</c>, <c>cli</c>, …).</summary>
    public string ClientName { get; init; } = "engine";

    /// <summary>Directory holding <c>cookies.db</c>, <c>history.db</c> and the secret-provider
    /// config. Null shares the desktop app's <c>%LocalAppData%/Vegha</c>.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Persist cookies to the shared jar. Off gives an in-memory jar for the process.</summary>
    public bool PersistCookies { get; init; } = true;

    /// <summary>Open the shared history database. Off skips history entirely (CI runs).</summary>
    public bool UseHistory { get; init; } = true;

    /// <summary>Allow OAuth2 authorization-code (browser) flows. Headless hosts leave it off.</summary>
    public bool AllowInteractiveAuth { get; init; }

    /// <summary>Opens the authorization URL for interactive OAuth2. Null uses the OS browser.
    /// The VS Code sidecar routes this to <c>vscode.env.openExternal</c>.</summary>
    public Func<string, Task>? BrowserLauncher { get; init; }

    /// <summary>Apply the user's <c>settings.json</c> (proxy, CAs, timeouts, history retention).</summary>
    public bool ApplyUserSettings { get; init; } = true;
}

/// <summary>
/// The UI-free service graph every send needs — the same objects the desktop app registers in
/// DI (<c>ServiceRegistration</c>), wired by hand so the CLI and the VS Code sidecar don't pull
/// in a container: HTTP executor with the shared cookie jar, the Jint script host with
/// <c>bru.sendRequest</c> / <c>bru.cookies</c>, one OAuth2 token cache, the history store,
/// secret-manager resolution and the session runtime-variable store.
/// </summary>
public sealed class EngineServices : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _oauthClient;

    public EngineOptions Options { get; }
    public CookieJarStore? Cookies { get; }
    public HttpExecutor Http { get; }
    public JintHost Scripting { get; }
    public OAuth2TokenAcquirer OAuth2 { get; }
    public HistoryStore? History { get; }
    public SecretRegistry Secrets { get; } = new();
    public RuntimeVariableStore RuntimeVariables { get; } = new();
    public LastResponseCache ResponseCache { get; } = new();

    /// <summary>Pipeline options carrying this graph's token cache and secret resolution.</summary>
    public RequestPipeline.Options PipelineOptions { get; }

    public EngineServices(EngineOptions? options = null)
    {
        Options = options ?? new EngineOptions();
        var dataDir = Options.DataDirectory;

        if (Options.PersistCookies)
        {
            Cookies = new CookieJarStore(dataDir is null
                ? CookieJarStore.DefaultDatabasePath()
                : Path.Combine(dataDir, "cookies.db"));
            Cookies.BeginInitialLoad();
        }

        _httpClient = new HttpClient();
        Http = new HttpExecutor(_httpClient, Cookies);
        Scripting = new JintHost(
            subRequestExecutor: new BruExecutorAdapter(Http),
            cookieJar: Cookies is null ? null : new CookieJarBruAdapter(Cookies));

        _oauthClient = new HttpClient();
        OAuth2 = new OAuth2TokenAcquirer(_oauthClient);
        if (Options.BrowserLauncher is { } launcher) OAuth2.BrowserLauncher = launcher;

        if (Options.UseHistory)
        {
            History = new HistoryStore(dataDir is null
                ? HistoryStore.DefaultDatabasePath()
                : Path.Combine(dataDir, "history.db"))
            {
                ClientName = Options.ClientName,
            };
        }

        ReloadSecretProviders();
        if (Options.ApplyUserSettings) ReloadSettings();

        PipelineOptions = new RequestPipeline.Options
        {
            OAuth2 = OAuth2,
            AllowInteractiveAuth = Options.AllowInteractiveAuth,
            ResolveSecrets = async (vars, ct) =>
                await Secrets.ResolveSecretsAsync(vars, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Re-reads the encrypted secret-provider config (call when it changes on disk).</summary>
    public void ReloadSecretProviders()
    {
        try
        {
            SecretProviderCatalog.Reload(Secrets, Options.DataDirectory is null
                ? new SecretProviderConfigStore()
                : new SecretProviderConfigStore(Options.DataDirectory));
        }
        catch { /* unreadable config — secret:// refs stay unresolved and are reported */ }
    }

    /// <summary>Re-reads <c>settings.json</c> and applies it (call when it changes on disk).</summary>
    public void ReloadSettings() =>
        EngineSettings.Apply(new AppSettingsStore().Reload(), Http, History);

    public void Dispose()
    {
        Cookies?.Dispose();
        History?.Dispose();
        _httpClient.Dispose();
        _oauthClient.Dispose();
    }
}
