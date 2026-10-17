using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;

namespace Vegha.App.Services;

/// <summary>Pushes Settings → Git / Network values into the git stack: executable override,
/// GCM / TLS / SSH options, GitHub Enterprise hosts, and the proxy git should use. Applied at
/// startup and again whenever settings are saved.</summary>
public static class GitSettingsApplier
{
    public static void Attach(GitIntegration git, AppSettingsStore store)
    {
        Apply(git, store.Load());
        store.Changed += s => Apply(git, s);
    }

    public static void Apply(GitIntegration git, AppSettings s)
    {
        git.Locator.OverridePath = s.GitExecutablePath;
        git.Broker.Options = new GitGlobalOptions
        {
            UseWindowsBroker = s.GitUseWindowsBroker,
            AzureReposOAuth = s.GitAzureReposOAuth,
            UseOsCertificateStore = s.GitUseOsCertificateStore,
            PreferWindowsOpenSsh = s.GitPreferWindowsOpenSsh,
        };
        GitProviderRegistry.EnterpriseGitHubHosts = s.GitEnterpriseGitHubHosts?.ToArray() ?? Array.Empty<string>();
        var proxy = BuildProxy(s);
        git.Broker.ProxyResolver = proxy is null ? null : _ => proxy;
        GitProviderRegistry.HandlerFactory = proxy is null ? null : () => BuildHandler(s);
    }

    /// <summary>Proxy-aware handler for provider REST calls (repo picker / create repo).</summary>
    private static HttpMessageHandler BuildHandler(AppSettings s)
    {
        var scheme = s.ProxyProtocol is "socks4" or "socks5" or "https" ? s.ProxyProtocol : "http";
        var proxy = new System.Net.WebProxy(new Uri($"{scheme}://{s.ProxyHost.Trim()}{(s.ProxyPort > 0 ? ":" + s.ProxyPort : "")}"))
        {
            BypassList = (s.ProxyBypass ?? string.Empty)
                .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => System.Text.RegularExpressions.Regex.Escape(h).Replace("\\*", ".*", StringComparison.Ordinal))
                .ToArray(),
        };
        if (s.ProxyAuthEnabled && !string.IsNullOrEmpty(s.ProxyUsername))
            proxy.Credentials = new System.Net.NetworkCredential(s.ProxyUsername, ProxyCredentialProtector.Unprotect(s.ProxyPasswordEncrypted));
        return new HttpClientHandler { Proxy = proxy, UseProxy = true };
    }

    /// <summary>Proxy for git from Network settings. "system" and "pac" leave git to its own
    /// configuration / HTTPS_PROXY (PAC isn't evaluated by Vegha yet); "on" maps to
    /// <c>http.proxy</c>, including credentials when proxy auth is enabled.</summary>
    public static GitProxy? BuildProxy(AppSettings s)
    {
        if (!s.GitUseAppProxy || !string.Equals(s.ProxyMode, "on", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(s.ProxyHost)) return null;
        var scheme = s.ProxyProtocol switch
        {
            "socks5" => "socks5h",
            "socks4" => "socks4a",
            "https" => "https",
            _ => "http",
        };
        var auth = string.Empty;
        if (s.ProxyAuthEnabled && !string.IsNullOrEmpty(s.ProxyUsername))
        {
            var password = ProxyCredentialProtector.Unprotect(s.ProxyPasswordEncrypted);
            auth = Uri.EscapeDataString(s.ProxyUsername) + (string.IsNullOrEmpty(password) ? "" : ":" + Uri.EscapeDataString(password)) + "@";
        }
        var port = s.ProxyPort > 0 ? ":" + s.ProxyPort : string.Empty;
        var noProxy = string.Join(",", (s.ProxyBypass ?? string.Empty)
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new GitProxy($"{scheme}://{auth}{s.ProxyHost.Trim()}{port}", string.IsNullOrEmpty(noProxy) ? null : noProxy);
    }
}
