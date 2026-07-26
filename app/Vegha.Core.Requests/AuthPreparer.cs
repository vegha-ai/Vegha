using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>Everything an <see cref="AuthConfig"/> contributes to one outgoing request, once
/// resolved. Produced by <see cref="AuthPreparer.PrepareAsync"/>.</summary>
public sealed record PreparedAuth(
    /// <summary>The URL after any query-param placement (API key / OAuth2 "add token to
    /// queryparams"). Unchanged for header-based schemes.</summary>
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    /// <summary>Set for NTLM — the executor swaps to a credential-carrying handler.</summary>
    NetworkCredential? NtlmCredential,
    /// <summary>The config that was actually applied. For OAuth2 this is the post-exchange
    /// token carrier (Bearer / API-key header / query param), not the OAuth2 config itself.</summary>
    AuthConfig? Effective,
    /// <summary>Token exchange outcome for OAuth2, so a UI host can echo it into its panel.
    /// Null for every other scheme.</summary>
    OAuth2TokenResult? OAuth2Token,
    string? ErrorMessage)
{
    public bool IsError => ErrorMessage is not null;

    public static PreparedAuth None(string url) =>
        new(url, Array.Empty<KeyValuePair<string, string>>(), null, null, null, null);

    public static PreparedAuth Failed(string url, string error) =>
        new(url, Array.Empty<KeyValuePair<string, string>>(), null, null, null, error);
}

/// <summary>
/// The single place that knows how to turn an <see cref="AuthConfig"/> into wire effects,
/// for every auth type. Callers pass the config that <see cref="RequestComposition"/> resolved
/// — so auth defined on a collection or folder behaves exactly like auth defined on the
/// request, including the multi-step schemes (OAuth2 token exchange, AWS SigV4 signing,
/// Digest challenge/response, NTLM credentials).
///
/// Work splits across two stages because signatures depend on the final request:
///   1. <see cref="PrepareAsync"/> — token acquisition, static headers, URL query placement,
///      NTLM credentials. Runs before the body is composed.
///   2. <see cref="ApplySignatures"/> — AWS SigV4, OAuth1, WSSE. Runs once the final URL,
///      header list and body exist, since those are inputs to the signature.
/// Digest is a third shape (challenge/response): see <see cref="UsesDigest"/> +
/// <see cref="BuildDigestRetry"/>, driven by the caller's 401 handling.
/// </summary>
public static class AuthPreparer
{
    /// <summary>Stage 1 — resolve everything that doesn't depend on the composed body.
    /// Never throws; token-acquisition failures land in <see cref="PreparedAuth.ErrorMessage"/>.</summary>
    public static async Task<PreparedAuth> PrepareAsync(
        AuthConfig? auth,
        string url,
        IReadOnlyDictionary<string, string> vars,
        OAuth2TokenAcquirer? oauth2 = null,
        CancellationToken cancellationToken = default)
    {
        if (auth is null || auth.Type is AuthType.None or AuthType.Inherit)
            return PreparedAuth.None(url);

        switch (auth.Type)
        {
            case AuthType.OAuth2:
            {
                if (oauth2 is null)
                    return PreparedAuth.Failed(url, "OAuth2 requires a token acquirer.");

                OAuth2TokenResult token;
                try
                {
                    token = await AcquireOAuth2Async(auth, vars, oauth2, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return PreparedAuth.Failed(url, $"OAuth2 token acquisition failed: {ex.Message}");
                }

                if (!token.IsSuccess || string.IsNullOrEmpty(token.AccessToken))
                    return PreparedAuth.Failed(url, token.ErrorMessage ?? "OAuth2 token acquisition failed.");

                // Turn the acquired token into a carrier config honoring "add token to" +
                // "header prefix", then let the ordinary applier place it.
                var carrier = BuildTokenCarrier(auth, token.AccessToken!);
                var placed = AuthApplier.Apply(carrier, url, vars);
                return new PreparedAuth(placed.Url, placed.Headers, null, carrier, token, null);
            }

            case AuthType.Ntlm:
            {
                // NTLM is negotiated by the handler, not by a header we can precompute.
                return new PreparedAuth(
                    url, Array.Empty<KeyValuePair<string, string>>(),
                    ReadNtlmCredential(auth, vars), auth, null, null);
            }

            case AuthType.Digest:
            case AuthType.AwsV4:
            case AuthType.OAuth1:
            case AuthType.Wsse:
                // Nothing to contribute yet — these land in ApplySignatures / the digest retry.
                return new PreparedAuth(
                    url, Array.Empty<KeyValuePair<string, string>>(), null, auth, null, null);

            default:
            {
                // Bearer / Basic / ApiKey — straight through the applier.
                var applied = AuthApplier.Apply(auth, url, vars);
                return new PreparedAuth(applied.Url, applied.Headers, null, auth, null, null);
            }
        }
    }

    /// <summary>Stage 2 — schemes whose value is computed over the final request. Appends to
    /// <paramref name="headers"/> in place. No-op for every other auth type.</summary>
    public static void ApplySignatures(
        AuthConfig? auth,
        string method,
        Uri uri,
        List<KeyValuePair<string, string>> headers,
        string body,
        IReadOnlyDictionary<string, string> vars)
    {
        if (auth is null) return;

        switch (auth.Type)
        {
            case AuthType.AwsV4:
            {
                var sig = AwsV4Signer.SignFromAuthConfig(auth, method, uri, headers, body, vars);
                if (sig is null) return;
                headers.Add(new KeyValuePair<string, string>("X-Amz-Date", sig.XAmzDate));
                headers.Add(new KeyValuePair<string, string>("X-Amz-Content-Sha256", sig.XAmzContentSha256));
                if (sig.XAmzSecurityToken is not null)
                    headers.Add(new KeyValuePair<string, string>("X-Amz-Security-Token", sig.XAmzSecurityToken));
                headers.Add(new KeyValuePair<string, string>("Authorization", sig.Authorization));
                break;
            }

            case AuthType.OAuth1:
            {
                var consumerKey = Read(auth, "consumerKey", vars);
                if (string.IsNullOrEmpty(consumerKey)) return;
                var token = Read(auth, "token", vars);
                var tokenSecret = Read(auth, "tokenSecret", vars);
                var realm = Raw(auth, "realm");
                var header = OAuth1Signer.BuildAuthorizationHeader(
                    new OAuth1Signer.Config(
                        ConsumerKey: consumerKey,
                        ConsumerSecret: Read(auth, "consumerSecret", vars),
                        SignatureMethod: Fallback(Raw(auth, "signatureMethod"), "HMAC-SHA1"),
                        Token: string.IsNullOrEmpty(token) ? null : token,
                        TokenSecret: string.IsNullOrEmpty(tokenSecret) ? null : tokenSecret,
                        Realm: string.IsNullOrEmpty(realm) ? null : realm),
                    method, uri.ToString());
                headers.Add(new KeyValuePair<string, string>("Authorization", header));
                break;
            }

            case AuthType.Wsse:
            {
                var username = Read(auth, "username", vars);
                if (string.IsNullOrEmpty(username)) return;
                headers.Add(new KeyValuePair<string, string>(
                    "X-WSSE", BuildWsseHeader(username, Read(auth, "password", vars))));
                break;
            }
        }
    }

    /// <summary>True when the caller must be ready to answer a 401 Digest challenge.</summary>
    public static bool UsesDigest(AuthConfig? auth) =>
        auth is not null && auth.Type == AuthType.Digest &&
        !string.IsNullOrEmpty(Raw(auth, "username"));

    /// <summary>Parses the <c>WWW-Authenticate</c> challenge off a 401 and builds the
    /// <c>Authorization</c> value for the retry. Null when no Digest challenge is present or
    /// the config has no username.</summary>
    public static string? BuildDigestRetry(
        AuthConfig? auth,
        IEnumerable<KeyValuePair<string, string>> responseHeaders,
        string method,
        Uri uri,
        IReadOnlyDictionary<string, string>? vars = null)
    {
        if (auth is null || auth.Type != AuthType.Digest) return null;
        var username = Read(auth, "username", vars);
        if (string.IsNullOrEmpty(username)) return null;
        var password = Read(auth, "password", vars);

        // RFC 7235 allows multiple WWW-Authenticate headers; take the first Digest one.
        foreach (var (name, value) in responseHeaders)
        {
            if (!string.Equals(name, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)) continue;
            if (DigestAuthenticator.TryParseChallenge(value, out var challenge) && challenge is not null)
            {
                return DigestAuthenticator
                    .BuildAuthorizationHeader(challenge, method, uri.PathAndQuery, username, password)
                    .Value;
            }
        }
        return null;
    }

    // ---- OAuth2 ------------------------------------------------------------

    /// <summary>Runs the grant flow described by <paramref name="auth"/>. Grant type comes from
    /// the config, so a collection-level OAuth2 block drives the same exchange a request-level
    /// one would.</summary>
    public static Task<OAuth2TokenResult> AcquireOAuth2Async(
        AuthConfig auth,
        IReadOnlyDictionary<string, string> vars,
        OAuth2TokenAcquirer acquirer,
        CancellationToken cancellationToken = default)
    {
        var tokenParams = AuthParameters.ToAcquirerParams(
            AuthParameters.DeserializeAdditionalParams(Raw(auth, "additional_token_params")));
        var refreshParams = AuthParameters.ToAcquirerParams(
            AuthParameters.DeserializeAdditionalParams(Raw(auth, "additional_refresh_params")));
        var refreshUrl = Raw(auth, "refresh_token_url");
        refreshUrl = string.IsNullOrWhiteSpace(refreshUrl) ? null : refreshUrl;

        var tokenUrl = Raw(auth, "access_token_url");
        var clientId = Raw(auth, "client_id");
        var clientSecret = Raw(auth, "client_secret");
        var scopeRaw = Raw(auth, "scope");
        var scope = string.IsNullOrWhiteSpace(scopeRaw) ? null : scopeRaw;
        var placement = Fallback(Raw(auth, "credentials_placement"), "body");
        var tokenId = Fallback(Raw(auth, "token_id"), "credentials");
        var tokenSource = Fallback(Raw(auth, "token_source"), "access_token");

        return Fallback(Raw(auth, "grant_type"), "client_credentials") switch
        {
            "password" => acquirer.AcquirePasswordAsync(
                new OAuth2PasswordConfig(
                    TokenUrl: tokenUrl,
                    ClientId: clientId,
                    ClientSecret: clientSecret,
                    Username: Raw(auth, "username"),
                    Password: Raw(auth, "password"),
                    Scope: scope,
                    CredentialsPlacement: placement,
                    AdditionalParameters: tokenParams,
                    TokenId: tokenId,
                    TokenSource: tokenSource,
                    RefreshTokenUrl: refreshUrl,
                    RefreshParameters: refreshParams),
                vars, cancellationToken),

            "authorization_code" => acquirer.AcquireAuthorizationCodeAsync(
                new OAuth2AuthorizationCodeConfig(
                    AuthorizationUrl: Raw(auth, "authorization_url"),
                    TokenUrl: tokenUrl,
                    ClientId: clientId,
                    ClientSecret: clientSecret,
                    CallbackUrl: Raw(auth, "callback_url"),
                    Scope: scope,
                    State: string.IsNullOrWhiteSpace(Raw(auth, "state")) ? null : Raw(auth, "state"),
                    UsePkce: !string.Equals(Raw(auth, "use_pkce"), "false", StringComparison.OrdinalIgnoreCase),
                    CredentialsPlacement: placement,
                    AdditionalParameters: tokenParams,
                    TokenId: tokenId,
                    TokenSource: tokenSource,
                    RefreshTokenUrl: refreshUrl,
                    RefreshParameters: refreshParams),
                vars, cancellationToken),

            _ => acquirer.AcquireClientCredentialsAsync(
                new OAuth2ClientCredentialsConfig(
                    TokenUrl: tokenUrl,
                    ClientId: clientId,
                    ClientSecret: clientSecret,
                    Scope: scope,
                    CredentialsPlacement: placement,
                    AdditionalParameters: tokenParams,
                    TokenId: tokenId,
                    TokenSource: tokenSource,
                    RefreshTokenUrl: refreshUrl,
                    RefreshParameters: refreshParams),
                vars, cancellationToken),
        };
    }

    /// <summary>Wraps an acquired access token in the config that places it on the wire,
    /// honoring the panel's "Add token to" + "Header Prefix". The defaults (headers +
    /// "Bearer") produce the classic <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public static AuthConfig BuildTokenCarrier(AuthConfig oauth2, string accessToken)
    {
        var addTo = Fallback(Raw(oauth2, "add_token_to"), "headers");
        var prefix = oauth2.Parameters.TryGetValue("header_prefix", out var hp) ? hp : "Bearer";

        if (string.Equals(addTo, "queryparams", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "access_token",
                    ["value"] = accessToken,
                    ["placement"] = "queryparams",
                },
            };
        }

        // A non-Bearer prefix can't go through the Bearer applier (it would re-prefix), so
        // deliver it as a verbatim Authorization header instead. "body" placement is rarely
        // useful outbound — treat it as the header default, matching Bruno.
        if (!string.IsNullOrEmpty(prefix) && !string.Equals(prefix, "Bearer", StringComparison.Ordinal))
        {
            return new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "Authorization",
                    ["value"] = prefix + " " + accessToken,
                    ["placement"] = "header",
                },
            };
        }

        return new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = accessToken },
        };
    }

    // ---- per-scheme readers ------------------------------------------------

    public static NetworkCredential? ReadNtlmCredential(
        AuthConfig auth, IReadOnlyDictionary<string, string>? vars = null)
    {
        var user = Read(auth, "username", vars);
        if (string.IsNullOrEmpty(user)) return null;
        var pass = Read(auth, "password", vars);
        var domain = Read(auth, "domain", vars);
        return string.IsNullOrEmpty(domain)
            ? new NetworkCredential(user, pass)
            : new NetworkCredential(user, pass, domain);
    }

    /// <summary>WSSE UsernameToken: PasswordDigest = base64(sha1(nonce + created + password)),
    /// with a fresh nonce + timestamp per call.</summary>
    private static string BuildWsseHeader(string username, string password)
    {
        var nonceBytes = new byte[16];
        RandomNumberGenerator.Fill(nonceBytes);
        var created = DateTime.UtcNow.ToString("o");
        var digest = SHA1.HashData(
            nonceBytes.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray());
        return $"UsernameToken Username=\"{username}\", " +
               $"PasswordDigest=\"{Convert.ToBase64String(digest)}\", " +
               $"Nonce=\"{Convert.ToBase64String(nonceBytes)}\", Created=\"{created}\"";
    }

    private static string Raw(AuthConfig auth, string key) =>
        auth.Parameters.TryGetValue(key, out var v) ? v : string.Empty;

    private static string Read(AuthConfig auth, string key, IReadOnlyDictionary<string, string>? vars) =>
        vars is null ? Raw(auth, key) : Interpolator.Resolve(Raw(auth, key), vars);

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}
