using System.Net;

namespace Vegha.Core.Persistence;

/// <summary>Builds an <see cref="IWebProxy"/> instance from the structured proxy fields
/// on <see cref="AppSettings"/>. Encapsulates the four supported modes:
/// <list type="bullet">
/// <item><c>off</c> — returns null; HTTP handler skips proxy entirely.</item>
/// <item><c>on</c> — builds a <see cref="WebProxy"/> for HTTP/HTTPS. SOCKS protocols are
///       accepted in settings but not honored at runtime (no built-in SOCKS support in
///       HttpClient as of .NET 10); the resolver returns null for those and emits no error.</item>
/// <item><c>system</c> — defers to <see cref="WebRequest.GetSystemWebProxy"/>.</item>
/// <item><c>pac</c> — not supported at runtime; falls back to system proxy.</item>
/// </list>
/// </summary>
public static class ProxyResolver
{
    public static IWebProxy? Build(AppSettings settings)
    {
        switch (settings.ProxyMode?.ToLowerInvariant())
        {
            case "off" or null or "":
                return null;

            case "system":
                try { return WebRequest.GetSystemWebProxy(); }
                catch { return null; }

            case "pac":
                // No PAC evaluator available; fall back so requests still flow through the
                // user's OS proxy (closest sane default until JS-engine support lands).
                try { return WebRequest.GetSystemWebProxy(); }
                catch { return null; }

            case "on":
                return BuildExplicitProxy(settings);

            default:
                return null;
        }
    }

    private static IWebProxy? BuildExplicitProxy(AppSettings settings)
    {
        var protocol = string.IsNullOrWhiteSpace(settings.ProxyProtocol)
            ? "http"
            : settings.ProxyProtocol.ToLowerInvariant();

        // HttpClient (through .NET 10) only ships built-in HTTP/HTTPS proxy support.
        // SOCKS proxies require an external handler (e.g. MihaZupan.HttpToSocks5Proxy). For now persist
        // the choice but skip the proxy at runtime — the user gets the same behavior as
        // ProxyMode = "off" until SOCKS support is wired.
        if (protocol is "socks4" or "socks5") return null;

        if (string.IsNullOrWhiteSpace(settings.ProxyHost) || settings.ProxyPort <= 0)
            return null;

        var url = $"{protocol}://{settings.ProxyHost}:{settings.ProxyPort}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var proxy = new WebProxy(uri)
        {
            BypassProxyOnLocal = false,
        };

        if (!string.IsNullOrWhiteSpace(settings.ProxyBypass))
        {
            // WebProxy.BypassList expects regex patterns. Users type glob-ish hostnames
            // ("localhost", "*.corp.example.com", "10.0.0.0/8"), so we escape regex
            // metacharacters and re-introduce `*` as `.*`. This keeps the user-facing
            // syntax familiar while satisfying the underlying API.
            proxy.BypassList = settings.ProxyBypass
                .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(GlobToRegex)
                .ToArray();
        }

        if (settings.ProxyAuthEnabled && !string.IsNullOrEmpty(settings.ProxyUsername))
        {
            var password = ProxyCredentialProtector.Unprotect(settings.ProxyPasswordEncrypted);
            proxy.Credentials = new NetworkCredential(settings.ProxyUsername, password);
        }

        return proxy;
    }

    private static string GlobToRegex(string glob)
    {
        // Escape regex metacharacters, then translate `*` (which Regex.Escape turned into
        // `\*`) into `.*`. Anchored loose match — no ^/$ so users typing "internal" match
        // "internal.example.com" or "host.internal" without surprise.
        var escaped = System.Text.RegularExpressions.Regex.Escape(glob);
        return escaped.Replace("\\*", ".*");
    }
}
