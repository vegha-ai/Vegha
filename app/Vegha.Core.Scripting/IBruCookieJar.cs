namespace Vegha.Core.Scripting;

/// <summary>
/// Layering boundary for the cookie jar — Scripting must not depend on Requests / SQLite.
/// Bruno's <c>bru.cookies.jar()</c> returns an object with this surface (get / set / unset /
/// getAll / clear). The Requests-layer <c>CookieJarBruAdapter</c> implements this interface
/// over <c>CookieJarStore</c>; tests can supply an in-memory fake.
///
/// Methods are intentionally lowercase + Postman-shaped (<c>getCookie</c> / <c>setCookie</c>
/// / <c>deleteCookie</c>) so Postman-translated scripts hit the right names without further
/// renaming.
/// </summary>
public interface IBruCookieJar
{
    /// <summary>Returns the cookie value for <paramref name="name"/> under <paramref name="url"/>,
    /// or null when absent.</summary>
    string? getCookie(string url, string name);

    /// <summary>Insert-or-replace a cookie for <paramref name="url"/>.</summary>
    void setCookie(string url, string name, string value);

    /// <summary>Removes a single cookie by name under <paramref name="url"/>.</summary>
    void deleteCookie(string url, string name);

    /// <summary>Returns every cookie that <paramref name="url"/> would send (i.e. matches
    /// the domain / path scope of the URL). Each entry is a <c>{ name, value, domain, path }</c>
    /// object friendly to JS consumers.</summary>
    List<CookieEntry> getCookies(string url);

    /// <summary>Removes every cookie that <paramref name="url"/> would send.</summary>
    void deleteCookies(string url);
}

/// <summary>A single cookie row exposed to scripts — kept lowercase so JS reads as
/// <c>c.name</c> / <c>c.value</c> per Bruno + Postman convention.</summary>
public sealed class CookieEntry
{
    public string name { get; set; } = string.Empty;
    public string value { get; set; } = string.Empty;
    public string domain { get; set; } = string.Empty;
    public string path { get; set; } = "/";

    public CookieEntry() { }
    public CookieEntry(string name, string value, string domain, string path)
    {
        this.name = name;
        this.value = value;
        this.domain = domain;
        this.path = path;
    }
}

/// <summary>The object returned by <c>bru.cookies</c> — exposes <c>jar()</c> which yields
/// the <see cref="IBruCookieJar"/>. Two-step lookup mirrors Bruno's API exactly: scripts
/// call <c>bru.cookies.jar().getCookie(url, name)</c>, never <c>bru.cookies.getCookie(...)</c>.</summary>
public sealed class BruCookiesApi
{
    private readonly IBruCookieJar? _jar;

    public BruCookiesApi(IBruCookieJar? jar) { _jar = jar; }

    public IBruCookieJar jar()
    {
        if (_jar is null)
            throw new InvalidOperationException(
                "bru.cookies.jar() is not available in this context (no cookie jar supplied to JintHost).");
        return _jar;
    }
}
