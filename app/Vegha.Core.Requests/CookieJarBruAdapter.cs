using System.Net;
using Vegha.Core.Scripting;

namespace Vegha.Core.Requests;

/// <summary>
/// Adapts <see cref="CookieJarStore"/> to the Scripting-layer <see cref="IBruCookieJar"/>
/// surface used by <c>bru.cookies.jar()</c>. Keeps <c>CookieJarStore</c> free of any
/// Scripting-shaped concepts so the dependency direction stays Requests → Scripting.
///
/// Cookie writes are fire-and-forget at the script level — Bruno's API is synchronous,
/// so we let the persistence task run in the background. The in-memory
/// <see cref="CookieContainer"/> sees mutations immediately, which is what matters for any
/// follow-on requests in the same run.
/// </summary>
public sealed class CookieJarBruAdapter : IBruCookieJar
{
    private readonly CookieJarStore _store;

    public CookieJarBruAdapter(CookieJarStore store) { _store = store; }

    public string? getCookie(string url, string name)
    {
        if (!TryUri(url, out var uri)) return null;
        var match = _store.Container.GetCookies(uri!).Cast<Cookie>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        return match?.Value;
    }

    public void setCookie(string url, string name, string value)
    {
        if (!TryUri(url, out var uri)) return;
        // Domain defaults to the URL host; path defaults to "/" so the cookie applies broadly.
        _ = _store.UpsertAsync(uri!.Host, "/", name, value);
    }

    public void deleteCookie(string url, string name)
    {
        if (!TryUri(url, out var uri)) return;
        // Find the actual stored cookie (its domain may include a leading dot) then soft-delete.
        var match = _store.Container.GetCookies(uri!).Cast<Cookie>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (match is null) return;
        _ = _store.RemoveAsync(match.Domain, string.IsNullOrEmpty(match.Path) ? "/" : match.Path, name);
    }

    public List<CookieEntry> getCookies(string url)
    {
        var result = new List<CookieEntry>();
        if (!TryUri(url, out var uri)) return result;
        foreach (Cookie c in _store.Container.GetCookies(uri!))
        {
            result.Add(new CookieEntry(c.Name, c.Value, c.Domain,
                string.IsNullOrEmpty(c.Path) ? "/" : c.Path));
        }
        return result;
    }

    public void deleteCookies(string url)
    {
        if (!TryUri(url, out var uri)) return;
        foreach (Cookie c in _store.Container.GetCookies(uri!))
        {
            _ = _store.RemoveAsync(c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Name);
        }
    }

    private static bool TryUri(string url, out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri);
}
