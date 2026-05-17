using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>An extra key/value pair attached to a token (or refresh-token) request.
/// <see cref="SendIn"/> picks the wire location: "body" (default) / "headers" /
/// "queryparams". Mirrors Bruno's "Additional Parameters → Token / Refresh" rows.</summary>
public sealed record OAuth2AdditionalParam(string Key, string Value, string SendIn = "body");

/// <summary>Configuration for an OAuth2 client_credentials token request.</summary>
public sealed record OAuth2ClientCredentialsConfig(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope = null,
    /// <summary>"body" (default) sends client creds as form fields; "basic_auth_header" uses Authorization: Basic.</summary>
    string CredentialsPlacement = "body",
    /// <summary>Bruno-parity additional params merged into the token request.</summary>
    IReadOnlyList<OAuth2AdditionalParam>? AdditionalParameters = null,
    /// <summary>Cache-isolation label (Bruno's "Token ID"). Defaults to "credentials"
    /// — change to support multiple identities sharing the same client_id+scope.</summary>
    string TokenId = "credentials",
    /// <summary>Which field of the token response to use as the bearer token.
    /// "access_token" (default) / "id_token" / "refresh_token".</summary>
    string TokenSource = "access_token",
    /// <summary>Optional alternative endpoint used for refresh_token grant. Falls back
    /// to <see cref="TokenUrl"/> when empty.</summary>
    string? RefreshTokenUrl = null,
    /// <summary>Bruno-parity additional params merged into refresh_token requests.</summary>
    IReadOnlyList<OAuth2AdditionalParam>? RefreshParameters = null);

/// <summary>Configuration for an OAuth2 password grant token request.</summary>
public sealed record OAuth2PasswordConfig(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password,
    string? Scope = null,
    string CredentialsPlacement = "body",
    IReadOnlyList<OAuth2AdditionalParam>? AdditionalParameters = null,
    string TokenId = "credentials",
    string TokenSource = "access_token",
    string? RefreshTokenUrl = null,
    IReadOnlyList<OAuth2AdditionalParam>? RefreshParameters = null);

/// <summary>Configuration for an OAuth2 authorization_code grant. PKCE is on by default.</summary>
public sealed record OAuth2AuthorizationCodeConfig(
    string AuthorizationUrl,
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string CallbackUrl,
    string? Scope = null,
    string? State = null,
    bool UsePkce = true,
    string CredentialsPlacement = "body",
    IReadOnlyList<OAuth2AdditionalParam>? AdditionalParameters = null,
    string TokenId = "credentials",
    string TokenSource = "access_token",
    string? RefreshTokenUrl = null,
    IReadOnlyList<OAuth2AdditionalParam>? RefreshParameters = null);

public sealed record OAuth2TokenResult(
    bool IsSuccess,
    string? AccessToken,
    string? ErrorMessage,
    bool FromCache,
    /// <summary>Token type from the response (typically "Bearer"). Defaults to "Bearer"
    /// when the IdP omits it.</summary>
    string? TokenType = "Bearer",
    /// <summary>Refresh token from the response, when present. Used for auto-refresh.</summary>
    string? RefreshToken = null);

/// <summary>
/// Acquires OAuth2 tokens (client_credentials, password, authorization_code+PKCE) and
/// caches them by (tokenUrl, clientId, scope) until expires_in elapses. Mirrors
/// bruno-requests/src/auth/oauth2-helper.ts and bruno-electron/src/utils/oauth2.js.
///
/// authorization_code uses a loopback HttpListener and the system browser to capture
/// the redirect, then exchanges the code at the token endpoint. PKCE S256 is used by
/// default — public clients should keep it on; confidential clients can disable it.
/// </summary>
public sealed class OAuth2TokenAcquirer
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

    /// <summary>Refresh tokens this many seconds before they're due to expire.</summary>
    public static readonly TimeSpan ExpiryGuardWindow = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: callers can swap in a fake browser launcher so the auth-code flow
    /// can be exercised without actually opening a browser. Default uses Process.Start.</summary>
    public Func<string, Task> BrowserLauncher { get; set; } = DefaultBrowserLaunch;

    public OAuth2TokenAcquirer(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // -----------------------------------------------------------------------
    // client_credentials
    // -----------------------------------------------------------------------

    public async Task<OAuth2TokenResult> AcquireClientCredentialsAsync(
        OAuth2ClientCredentialsConfig config,
        IReadOnlyDictionary<string, string>? vars = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(config, vars);
        if (string.IsNullOrEmpty(resolved.TokenUrl) || string.IsNullOrEmpty(resolved.ClientId))
            return new OAuth2TokenResult(false, null, "OAuth2: token URL and client ID are required.", false);

        var cacheKey = $"cc|{resolved.TokenId}|{resolved.TokenUrl}|{resolved.ClientId}|{resolved.Scope ?? string.Empty}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.IsFresh)
            return new OAuth2TokenResult(true, cached.AccessToken, null, FromCache: true,
                TokenType: cached.TokenType, RefreshToken: cached.RefreshToken);

        // If the cache entry is stale but carries a refresh_token, prefer the refresh flow
        // over re-running the full grant — saves the round trips and avoids re-prompting on
        // grants like authorization_code.
        if (cached?.RefreshToken is { Length: > 0 } refreshTok)
        {
            var refreshed = await TryRefreshAsync(
                refreshTok, resolved.RefreshTokenUrl ?? resolved.TokenUrl,
                resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
                resolved.RefreshParameters, cacheKey, resolved.TokenSource,
                cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess) return refreshed;
            // fall through to a fresh grant on refresh failure
        }

        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (!string.IsNullOrEmpty(resolved.Scope)) form.Add(new("scope", resolved.Scope));

        return await PostTokenRequest(
            resolved.TokenUrl, resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
            form, cacheKey, resolved.TokenSource, resolved.AdditionalParameters,
            cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // password
    // -----------------------------------------------------------------------

    public async Task<OAuth2TokenResult> AcquirePasswordAsync(
        OAuth2PasswordConfig config,
        IReadOnlyDictionary<string, string>? vars = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(config, vars);
        if (string.IsNullOrEmpty(resolved.TokenUrl) || string.IsNullOrEmpty(resolved.Username))
            return new OAuth2TokenResult(false, null, "OAuth2 password: token URL and username are required.", false);

        var cacheKey = $"pw|{resolved.TokenId}|{resolved.TokenUrl}|{resolved.ClientId}|{resolved.Username}|{resolved.Scope ?? string.Empty}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.IsFresh)
            return new OAuth2TokenResult(true, cached.AccessToken, null, FromCache: true,
                TokenType: cached.TokenType, RefreshToken: cached.RefreshToken);

        if (cached?.RefreshToken is { Length: > 0 } refreshTok)
        {
            var refreshed = await TryRefreshAsync(
                refreshTok, resolved.RefreshTokenUrl ?? resolved.TokenUrl,
                resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
                resolved.RefreshParameters, cacheKey, resolved.TokenSource,
                cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess) return refreshed;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "password"),
            new("username", resolved.Username),
            new("password", resolved.Password),
        };
        if (!string.IsNullOrEmpty(resolved.Scope)) form.Add(new("scope", resolved.Scope));

        return await PostTokenRequest(
            resolved.TokenUrl, resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
            form, cacheKey, resolved.TokenSource, resolved.AdditionalParameters,
            cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // authorization_code (with PKCE)
    // -----------------------------------------------------------------------

    public async Task<OAuth2TokenResult> AcquireAuthorizationCodeAsync(
        OAuth2AuthorizationCodeConfig config,
        IReadOnlyDictionary<string, string>? vars = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(config, vars);
        if (string.IsNullOrEmpty(resolved.AuthorizationUrl) || string.IsNullOrEmpty(resolved.TokenUrl) ||
            string.IsNullOrEmpty(resolved.ClientId) || string.IsNullOrEmpty(resolved.CallbackUrl))
            return new OAuth2TokenResult(false, null,
                "OAuth2 auth-code: authorization URL, token URL, client ID, and callback URL are required.", false);

        var cacheKey = $"ac|{resolved.TokenId}|{resolved.TokenUrl}|{resolved.ClientId}|{resolved.Scope ?? string.Empty}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.IsFresh)
            return new OAuth2TokenResult(true, cached.AccessToken, null, FromCache: true,
                TokenType: cached.TokenType, RefreshToken: cached.RefreshToken);

        if (cached?.RefreshToken is { Length: > 0 } refreshTok)
        {
            var refreshed = await TryRefreshAsync(
                refreshTok, resolved.RefreshTokenUrl ?? resolved.TokenUrl,
                resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
                resolved.RefreshParameters, cacheKey, resolved.TokenSource,
                cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess) return refreshed;
        }

        // PKCE: code_verifier (43-128 char base64url) + code_challenge (S256(verifier) base64url-no-pad).
        var (codeVerifier, codeChallenge) = resolved.UsePkce ? GeneratePkcePair() : (null, null);
        var state = string.IsNullOrEmpty(resolved.State) ? GenerateState() : resolved.State;

        // Listen on the loopback path before opening the browser so the redirect can't race us.
        var callbackUri = new Uri(resolved.CallbackUrl);
        if (!IsLoopbackHttp(callbackUri))
            return new OAuth2TokenResult(false, null,
                $"OAuth2 auth-code callback URL must be a loopback http://127.0.0.1 or http://localhost address: got {resolved.CallbackUrl}", false);

        var prefix = $"http://{callbackUri.Host}:{callbackUri.Port}{callbackUri.AbsolutePath.TrimEnd('/')}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return new OAuth2TokenResult(false, null, $"OAuth2 auth-code listener failed to bind {prefix}: {ex.Message}", false);
        }

        var authUrl = BuildAuthorizationUrl(resolved, codeChallenge, state);
        try
        {
            await BrowserLauncher(authUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            listener.Close();
            return new OAuth2TokenResult(false, null, $"OAuth2 auth-code: failed to launch browser: {ex.Message}", false);
        }

        // Wait for the redirect — bound to a 5-minute hard timeout so we can't hang forever.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string? authCode;
        try
        {
            authCode = await WaitForCallback(listener, state, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new OAuth2TokenResult(false, null, "OAuth2 auth-code: timed out waiting for browser redirect.", false);
        }
        finally
        {
            try { listener.Close(); } catch { /* best-effort */ }
        }

        if (string.IsNullOrEmpty(authCode))
            return new OAuth2TokenResult(false, null, "OAuth2 auth-code: redirect did not include an authorization code.", false);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", authCode),
            new("redirect_uri", resolved.CallbackUrl),
        };
        if (!string.IsNullOrEmpty(codeVerifier)) form.Add(new("code_verifier", codeVerifier!));

        return await PostTokenRequest(
            resolved.TokenUrl, resolved.ClientId, resolved.ClientSecret, resolved.CredentialsPlacement,
            form, cacheKey, resolved.TokenSource, resolved.AdditionalParameters,
            linked.Token).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Cache controls
    // -----------------------------------------------------------------------

    public void InvalidateCache() => _cache.Clear();

    public void InvalidateCache(OAuth2ClientCredentialsConfig config, IReadOnlyDictionary<string, string>? vars = null)
    {
        var resolved = Resolve(config, vars);
        var key = $"cc|{resolved.TokenId}|{resolved.TokenUrl}|{resolved.ClientId}|{resolved.Scope ?? string.Empty}";
        _cache.TryRemove(key, out _);
    }

    /// <summary>Clear-cache hook for the UI's "Clear Cache" button. Removes every cached
    /// token whose key contains the given token id segment (the second pipe-delimited
    /// segment). Token-id is the user-facing isolation label set in the OAuth2 panel.</summary>
    public void InvalidateCacheForTokenId(string tokenId)
    {
        if (string.IsNullOrEmpty(tokenId)) { _cache.Clear(); return; }
        var infix = "|" + tokenId + "|";
        foreach (var key in _cache.Keys.ToList())
        {
            if (key.Contains(infix, StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
        }
    }

    // ============================== Helpers ==============================

    private async Task<OAuth2TokenResult> PostTokenRequest(
        string tokenUrl, string clientId, string clientSecret, string credentialsPlacement,
        List<KeyValuePair<string, string>> form, string cacheKey, string tokenSource,
        IReadOnlyList<OAuth2AdditionalParam>? additionalParameters,
        CancellationToken ct)
    {
        try
        {
            // Apply additional-parameter rows: body params merge into the form; queryparams
            // get appended to the URL; headers attach to the HttpRequestMessage.
            var (url, formExtras, headerExtras) = SplitAdditionalParams(tokenUrl, additionalParameters);
            foreach (var (k, v) in formExtras) form.Add(new(k, v));

            using var msg = new HttpRequestMessage(HttpMethod.Post, url);
            foreach (var (k, v) in headerExtras)
            {
                if (!msg.Headers.TryAddWithoutValidation(k, v))
                    msg.Content?.Headers.TryAddWithoutValidation(k, v);
            }

            if (string.Equals(credentialsPlacement, "basic_auth_header", StringComparison.OrdinalIgnoreCase))
            {
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            else
            {
                form.Add(new("client_id", clientId));
                if (!string.IsNullOrEmpty(clientSecret)) form.Add(new("client_secret", clientSecret));
            }
            msg.Content = new FormUrlEncodedContent(form);
            // Re-attach header extras now that Content exists in case the destination is on Content.
            foreach (var (k, v) in headerExtras)
            {
                if (!msg.Headers.Contains(k))
                    msg.Content.Headers.TryAddWithoutValidation(k, v);
            }

            using var resp = await _httpClient.SendAsync(msg, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new OAuth2TokenResult(false, null,
                    $"OAuth2 token endpoint returned {(int)resp.StatusCode}: {Truncate(body)}", false);

            var parsed = TryParseTokenResponse(body, tokenSource);
            if (parsed.Token is null)
                return new OAuth2TokenResult(false, null,
                    $"OAuth2 response missing {tokenSource}: {Truncate(body)}", false);

            var ttl = parsed.ExpiresIn.HasValue
                ? TimeSpan.FromSeconds(parsed.ExpiresIn.Value) - ExpiryGuardWindow
                : TimeSpan.FromMinutes(5);
            if (ttl < TimeSpan.Zero) ttl = TimeSpan.Zero;

            _cache[cacheKey] = new CachedToken(parsed.Token, DateTime.UtcNow + ttl, parsed.TokenType, parsed.RefreshToken);
            return new OAuth2TokenResult(true, parsed.Token, null, false,
                TokenType: parsed.TokenType, RefreshToken: parsed.RefreshToken);
        }
        catch (Exception ex)
        {
            return new OAuth2TokenResult(false, null, $"OAuth2 token request failed: {ex.Message}", false);
        }
    }

    /// <summary>POSTs a refresh_token grant request against <paramref name="refreshUrl"/>.
    /// On success the cache slot is replaced; failure leaves the existing slot untouched so
    /// the caller can fall through to a fresh grant.</summary>
    private async Task<OAuth2TokenResult> TryRefreshAsync(
        string refreshToken, string refreshUrl,
        string clientId, string clientSecret, string credentialsPlacement,
        IReadOnlyList<OAuth2AdditionalParam>? refreshParameters,
        string cacheKey, string tokenSource,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
        };
        return await PostTokenRequest(refreshUrl, clientId, clientSecret, credentialsPlacement,
            form, cacheKey, tokenSource, refreshParameters, ct).ConfigureAwait(false);
    }

    /// <summary>Splits the user-supplied additional-parameter rows into the three wire
    /// destinations: queryparams folded into the URL, body params returned as form pairs,
    /// header params returned as a pair list. Variable interpolation happens upstream
    /// in the per-config Resolve helpers.</summary>
    private static (string Url, List<KeyValuePair<string, string>> FormExtras, List<KeyValuePair<string, string>> HeaderExtras)
        SplitAdditionalParams(string url, IReadOnlyList<OAuth2AdditionalParam>? extras)
    {
        var formExtras = new List<KeyValuePair<string, string>>();
        var headerExtras = new List<KeyValuePair<string, string>>();
        if (extras is null || extras.Count == 0) return (url, formExtras, headerExtras);

        var queryAdds = new List<KeyValuePair<string, string>>();
        foreach (var p in extras)
        {
            if (string.IsNullOrEmpty(p.Key)) continue;
            switch (p.SendIn?.ToLowerInvariant())
            {
                case "headers":      headerExtras.Add(new(p.Key, p.Value ?? string.Empty)); break;
                case "queryparams":  queryAdds.Add(new(p.Key, p.Value ?? string.Empty));    break;
                default:             formExtras.Add(new(p.Key, p.Value ?? string.Empty));   break;
            }
        }

        if (queryAdds.Count > 0)
        {
            var ub = new UriBuilder(url);
            var qs = System.Web.HttpUtility.ParseQueryString(ub.Query ?? string.Empty);
            foreach (var (k, v) in queryAdds) qs[k] = v;
            ub.Query = qs.ToString();
            url = ub.Uri.ToString();
        }
        return (url, formExtras, headerExtras);
    }

    private static OAuth2ClientCredentialsConfig Resolve(
        OAuth2ClientCredentialsConfig cfg, IReadOnlyDictionary<string, string>? vars)
    {
        if (vars is null) return cfg;
        return cfg with
        {
            TokenUrl = Interpolator.Resolve(cfg.TokenUrl, vars),
            ClientId = Interpolator.Resolve(cfg.ClientId, vars),
            ClientSecret = Interpolator.Resolve(cfg.ClientSecret, vars),
            Scope = cfg.Scope is null ? null : Interpolator.Resolve(cfg.Scope, vars),
            RefreshTokenUrl = cfg.RefreshTokenUrl is null ? null : Interpolator.Resolve(cfg.RefreshTokenUrl, vars),
            AdditionalParameters = ResolveAdditional(cfg.AdditionalParameters, vars),
            RefreshParameters = ResolveAdditional(cfg.RefreshParameters, vars),
        };
    }

    private static OAuth2PasswordConfig Resolve(
        OAuth2PasswordConfig cfg, IReadOnlyDictionary<string, string>? vars)
    {
        if (vars is null) return cfg;
        return cfg with
        {
            TokenUrl = Interpolator.Resolve(cfg.TokenUrl, vars),
            ClientId = Interpolator.Resolve(cfg.ClientId, vars),
            ClientSecret = Interpolator.Resolve(cfg.ClientSecret, vars),
            Username = Interpolator.Resolve(cfg.Username, vars),
            Password = Interpolator.Resolve(cfg.Password, vars),
            Scope = cfg.Scope is null ? null : Interpolator.Resolve(cfg.Scope, vars),
            RefreshTokenUrl = cfg.RefreshTokenUrl is null ? null : Interpolator.Resolve(cfg.RefreshTokenUrl, vars),
            AdditionalParameters = ResolveAdditional(cfg.AdditionalParameters, vars),
            RefreshParameters = ResolveAdditional(cfg.RefreshParameters, vars),
        };
    }

    private static OAuth2AuthorizationCodeConfig Resolve(
        OAuth2AuthorizationCodeConfig cfg, IReadOnlyDictionary<string, string>? vars)
    {
        if (vars is null) return cfg;
        return cfg with
        {
            AuthorizationUrl = Interpolator.Resolve(cfg.AuthorizationUrl, vars),
            TokenUrl = Interpolator.Resolve(cfg.TokenUrl, vars),
            ClientId = Interpolator.Resolve(cfg.ClientId, vars),
            ClientSecret = Interpolator.Resolve(cfg.ClientSecret, vars),
            CallbackUrl = Interpolator.Resolve(cfg.CallbackUrl, vars),
            Scope = cfg.Scope is null ? null : Interpolator.Resolve(cfg.Scope, vars),
            State = cfg.State is null ? null : Interpolator.Resolve(cfg.State, vars),
            RefreshTokenUrl = cfg.RefreshTokenUrl is null ? null : Interpolator.Resolve(cfg.RefreshTokenUrl, vars),
            AdditionalParameters = ResolveAdditional(cfg.AdditionalParameters, vars),
            RefreshParameters = ResolveAdditional(cfg.RefreshParameters, vars),
        };
    }

    /// <summary>Interpolates the Key and Value of every additional parameter against the
    /// variable bag. Returns the input unchanged when no resolution is needed.</summary>
    private static IReadOnlyList<OAuth2AdditionalParam>? ResolveAdditional(
        IReadOnlyList<OAuth2AdditionalParam>? list, IReadOnlyDictionary<string, string> vars)
    {
        if (list is null || list.Count == 0) return list;
        var result = new List<OAuth2AdditionalParam>(list.Count);
        foreach (var p in list)
        {
            result.Add(new OAuth2AdditionalParam(
                Interpolator.Resolve(p.Key, vars),
                Interpolator.Resolve(p.Value, vars),
                p.SendIn));
        }
        return result;
    }

    private static string BuildAuthorizationUrl(
        OAuth2AuthorizationCodeConfig cfg, string? codeChallenge, string? state)
    {
        var ub = new UriBuilder(cfg.AuthorizationUrl);
        var qs = System.Web.HttpUtility.ParseQueryString(ub.Query ?? string.Empty);
        qs["response_type"] = "code";
        qs["client_id"] = cfg.ClientId;
        qs["redirect_uri"] = cfg.CallbackUrl;
        if (!string.IsNullOrEmpty(cfg.Scope)) qs["scope"] = cfg.Scope;
        if (!string.IsNullOrEmpty(state)) qs["state"] = state;
        if (!string.IsNullOrEmpty(codeChallenge))
        {
            qs["code_challenge"] = codeChallenge;
            qs["code_challenge_method"] = "S256";
        }
        ub.Query = qs.ToString();
        return ub.Uri.ToString();
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        Span<byte> verifierBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64UrlNoPad(verifierBytes);
        var challenge = Base64UrlNoPad(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlNoPad(bytes);
    }

    private static string Base64UrlNoPad(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsLoopbackHttp(Uri uri)
        => string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
           (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]");

    /// <summary>Awaits the browser redirect on the listener and pulls <c>code</c> from
    /// the query string. Validates <c>state</c> when one was sent. Always responds with
    /// a friendly HTML page so the user knows they can close the tab.</summary>
    private static async Task<string?> WaitForCallback(HttpListener listener, string? expectedState, CancellationToken ct)
    {
        while (true)
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (completed != contextTask) ct.ThrowIfCancellationRequested();

            var ctx = await contextTask.ConfigureAwait(false);
            var query = ctx.Request.Url?.Query ?? string.Empty;
            var parsed = System.Web.HttpUtility.ParseQueryString(query);
            var code = parsed["code"];
            var state = parsed["state"];
            var error = parsed["error"];

            try
            {
                using var resp = ctx.Response;
                resp.ContentType = "text/html; charset=utf-8";
                var html = error is not null
                    ? $"<html><body><h2>Authentication failed</h2><p>{WebUtility.HtmlEncode(error)}</p></body></html>"
                    : "<html><body><h2>Authentication complete</h2><p>You can close this tab and return to Vegha.</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                resp.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { /* best-effort response — do not let writeback failures swallow the code */ }

            if (error is not null) return null;
            if (string.IsNullOrEmpty(code)) continue; // ignore drive-by hits (favicon, etc.)
            if (!string.IsNullOrEmpty(expectedState) && state != expectedState) return null;
            return code;
        }
    }

    private static Task DefaultBrowserLaunch(string url)
    {
        var psi = new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(psi);
        return Task.CompletedTask;
    }

    private static (string? Token, int? ExpiresIn, string? TokenType, string? RefreshToken) TryParseTokenResponse(
        string body, string tokenSource)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? token = null;
            int? expiresIn = null;
            string? tokenType = null;
            string? refreshToken = null;

            // Bruno's "Token Source" picks which field becomes the bearer. Default access_token.
            var sourceKey = string.IsNullOrEmpty(tokenSource) ? "access_token" : tokenSource;
            if (root.TryGetProperty(sourceKey, out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString();
            // Fallback: if the configured source is absent but access_token exists, use that.
            if (token is null && sourceKey != "access_token" &&
                root.TryGetProperty("access_token", out var fallback) &&
                fallback.ValueKind == JsonValueKind.String)
                token = fallback.GetString();

            if (root.TryGetProperty("expires_in", out var e))
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) expiresIn = n;
                else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var ns)) expiresIn = ns;
            }
            if (root.TryGetProperty("token_type", out var tt) && tt.ValueKind == JsonValueKind.String)
                tokenType = tt.GetString();
            if (root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                refreshToken = rt.GetString();

            return (token, expiresIn, tokenType ?? "Bearer", refreshToken);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed record CachedToken(string AccessToken, DateTime ExpiresAt, string? TokenType = "Bearer", string? RefreshToken = null)
    {
        public bool IsFresh => DateTime.UtcNow < ExpiresAt;
    }
}
