namespace Vegha.Core.Persistence;

/// <summary>App-wide preferences persisted to <c>%LocalAppData%/Vegha/settings.json</c>.
/// New optional fields trail the older positional parameters so existing settings.json files
/// from prior versions still deserialize via System.Text.Json's default-parameter handling.</summary>
public sealed record AppSettings(
    string Theme,
    string FontFamily,
    int FontSize,
    string? HttpProxy,
    bool DefaultFollowRedirects,
    bool DefaultVerifySsl,
    bool DefaultSendCookies,
    bool DefaultSaveCookies,
    bool DefaultEncodeUrl,
    int RequestTimeoutSeconds,
    bool WelcomeShown = false,
    /// <summary>Custom Certificate Authority list. Each entry is either an absolute file
    /// path to a PEM-encoded cert/chain or the inline PEM block itself.</summary>
    IReadOnlyList<string>? CustomTrustCAs = null,

    // ─── Appearance ───
    /// <summary>"light" | "dark" | "system" — the base mode the OS chrome and Fluent theme follow.
    /// Supersedes the legacy <see cref="Theme"/> field; both are written for back-compat.</summary>
    string ThemeMode = "dark",
    /// <summary>Theme variant key used when ThemeMode resolves to light (e.g. "Light", "LightPastel", "VSCodeLight").</summary>
    string ThemeVariantLight = "Light",
    /// <summary>Theme variant key used when ThemeMode resolves to dark (e.g. "Dark", "DarkCatppuccin").</summary>
    string ThemeVariantDark = "Dark",
    /// <summary>UI zoom factor applied to the entire visual tree via LayoutTransform. Clamped 0.8–2.0.</summary>
    double InterfaceZoom = 1.0,

    // ─── Network → Proxy ───
    /// <summary>"off" | "on" | "system" | "pac" — proxy resolution mode.</summary>
    string ProxyMode = "off",
    /// <summary>"http" | "https" | "socks4" | "socks5" — protocol when ProxyMode = "on".</summary>
    string ProxyProtocol = "http",
    string ProxyHost = "",
    int ProxyPort = 0,
    bool ProxyAuthEnabled = false,
    string ProxyUsername = "",
    /// <summary>DPAPI-protected (Windows) or base64 (other) opaque password blob; never logged.</summary>
    string ProxyPasswordEncrypted = "",
    /// <summary>Comma- or newline-separated hostnames/patterns that bypass the proxy.</summary>
    string ProxyBypass = "",
    /// <summary>URL of the PAC script when ProxyMode = "pac". PAC evaluation is not yet supported
    /// at runtime; the value is persisted for future use.</summary>
    string? ProxyPacUrl = null,

    // ─── Network → TLS session cache ───
    /// <summary>Reuse TLS sessions/connections across requests for faster handshakes.</summary>
    bool CacheSslSessions = true,

    // ─── Request defaults ───
    /// <summary>Persist response bodies into the history store. Default false for compliance
    /// posture. When off, history rows are still recorded in full — method/url/status/duration/
    /// timestamp AND the request snapshot used by Replay (the user's own request, already
    /// persisted by the session-tab store); only the response payload is suppressed. Toggling
    /// off does not delete already-stored history.</summary>
    bool SaveResponsesToHistory = false,
    /// <summary>Truncate response previews (and history-store body persistence) larger than this.</summary>
    int MaxBodySizeMb = 50,
    /// <summary>Drop history rows older than this many days. 0 disables age pruning.</summary>
    int HistoryRetentionDays = 365,
    /// <summary>Cap on total history rows kept. Older rows are pruned on insert.</summary>
    int HistoryRetentionMaxEntries = 1000,

    // ─── Editor ───
    int EditorTabSize = 2,
    bool EditorWordWrap = true,
    bool EditorShowLineNumbers = true,

    // ─── Updates ───
    bool AutoCheckForUpdates = true,
    /// <summary>"stable" | "beta" — which Velopack feed to query. "beta" falls back to "stable"
    /// until a beta feed is published.</summary>
    string UpdateChannel = "stable")
{
    public static AppSettings Default { get; } = new(
        Theme: "dark",
        FontFamily: "JetBrains Mono",
        FontSize: 12,
        HttpProxy: null,
        DefaultFollowRedirects: true,
        DefaultVerifySsl: true,
        DefaultSendCookies: true,
        DefaultSaveCookies: true,
        DefaultEncodeUrl: true,
        RequestTimeoutSeconds: 100,
        WelcomeShown: false,
        CustomTrustCAs: null,
        ThemeMode: "dark",
        ThemeVariantLight: "Light",
        ThemeVariantDark: "Dark",
        InterfaceZoom: 1.0,
        ProxyMode: "off",
        ProxyProtocol: "http",
        ProxyHost: "",
        ProxyPort: 0,
        ProxyAuthEnabled: false,
        ProxyUsername: "",
        ProxyPasswordEncrypted: "",
        ProxyBypass: "",
        ProxyPacUrl: null,
        CacheSslSessions: true,
        SaveResponsesToHistory: false,
        MaxBodySizeMb: 50,
        HistoryRetentionDays: 365,
        HistoryRetentionMaxEntries: 1000,
        EditorTabSize: 2,
        EditorWordWrap: true,
        EditorShowLineNumbers: true,
        AutoCheckForUpdates: true,
        UpdateChannel: "stable");
}
