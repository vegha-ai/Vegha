using System.Net.Http.Headers;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.Conjur;

/// <summary>
/// CyberArk Conjur REST adapter. Conjur authenticates via API-key → short-lived access
/// token. The provider keeps the token cached until it expires (~8 minutes by default).
/// Path syntax: "account:variable:full/path/to/secret"; field selector unused.
/// </summary>
public sealed class ConjurProvider : ISecretProvider
{
    private readonly HttpClient _http;
    private readonly string _account;
    private readonly string _login;
    private readonly string _apiKey;
    private string? _accessToken;
    private DateTime _accessTokenExpiresAt;

    public string Name => "conjur";

    public ConjurProvider(string baseUrl, string account, string login, string apiKey, HttpClient? http = null)
    {
        _account = account;
        _login = login;
        _apiKey = apiKey;
        _http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        // path: "account:kind:identifier" — strip prefixes if the user passed them already.
        // Default kind to "variable" if only the identifier is present.
        var (account, kind, id) = ParsePath(path);
        var url = $"secrets/{Uri.EscapeDataString(account)}/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(id)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token=\"{token}\"");

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Conjur returned {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresAt)
            return _accessToken;

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"authn/{Uri.EscapeDataString(_account)}/{Uri.EscapeDataString(_login)}/authenticate")
        {
            Content = new StringContent(_apiKey, System.Text.Encoding.UTF8, "text/plain")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException(
            $"Conjur authenticate returned {(int)resp.StatusCode}");

        var token = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _accessToken = token;
        _accessTokenExpiresAt = DateTime.UtcNow.AddMinutes(7); // refresh well before the 8-min server TTL
        return token;
    }

    private (string Account, string Kind, string Id) ParsePath(string path)
    {
        var parts = path.Split(':', 3);
        return parts.Length switch
        {
            3 => (parts[0], parts[1], parts[2]),
            2 => (_account, parts[0], parts[1]),
            _ => (_account, "variable", path),
        };
    }
}
