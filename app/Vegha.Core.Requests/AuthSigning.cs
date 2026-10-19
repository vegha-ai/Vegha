using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>
/// The auth types <see cref="AuthApplier"/> can't express as static headers, because they
/// depend on the final URL / headers / body or on a server challenge:
/// AWS SigV4, OAuth1, WSSE (signed after composition), NTLM (handler credentials) and
/// Digest (a 401 challenge → retry). All read the flat <see cref="AuthConfig.Parameters"/>
/// keys the request editor and the .bru format store, and interpolate <c>{{var}}</c>s.
/// </summary>
public static class AuthSigning
{
    /// <summary>Auth types this class or <see cref="AuthApplier"/> can send without user
    /// interaction. OAuth2 is resolved to Bearer/ApiKey before either sees it.</summary>
    public static bool IsSendable(AuthConfig? auth) =>
        auth is null || auth.Type is not AuthType.OAuth2;

    /// <summary>Appends the signature headers for SigV4 / OAuth1 / WSSE. Call after every
    /// other header is in place: the signatures cover them. No-op for other auth types.</summary>
    public static void AppendSignatureHeaders(
        AuthConfig? auth,
        string method,
        Uri url,
        List<KeyValuePair<string, string>> headers,
        string? body,
        IReadOnlyDictionary<string, string> vars)
    {
        switch (auth?.Type)
        {
            case AuthType.AwsV4:
            {
                var sig = AwsV4Signer.SignFromAuthConfig(auth, method, url, headers, body ?? string.Empty, vars);
                if (sig is null) return;
                headers.Add(new("X-Amz-Date", sig.XAmzDate));
                headers.Add(new("X-Amz-Content-Sha256", sig.XAmzContentSha256));
                if (sig.XAmzSecurityToken is not null)
                    headers.Add(new("X-Amz-Security-Token", sig.XAmzSecurityToken));
                headers.Add(new("Authorization", sig.Authorization));
                return;
            }
            case AuthType.OAuth1:
            {
                var consumerKey = Get(auth, "consumerKey", vars);
                if (string.IsNullOrEmpty(consumerKey)) return;
                var signatureMethod = Get(auth, "signatureMethod", vars);
                var header = OAuth1Signer.BuildAuthorizationHeader(
                    new OAuth1Signer.Config(
                        ConsumerKey: consumerKey,
                        ConsumerSecret: Get(auth, "consumerSecret", vars),
                        SignatureMethod: string.IsNullOrEmpty(signatureMethod) ? "HMAC-SHA1" : signatureMethod,
                        Token: NullIfEmpty(Get(auth, "token", vars)),
                        TokenSecret: NullIfEmpty(Get(auth, "tokenSecret", vars)),
                        Realm: NullIfEmpty(Get(auth, "realm", vars))),
                    method, url.ToString());
                headers.Add(new("Authorization", header));
                return;
            }
            case AuthType.Wsse:
            {
                var username = Get(auth, "username", vars);
                if (string.IsNullOrEmpty(username)) return;
                headers.Add(new("X-WSSE", BuildWsseHeader(username, Get(auth, "password", vars), DateTime.UtcNow)));
                return;
            }
        }
    }

    /// <summary>WSSE UsernameToken header (header form — in-envelope inclusion is the SOAP
    /// security processor's job). PasswordDigest = base64(sha1(nonce + created + password)).</summary>
    public static string BuildWsseHeader(string username, string password, DateTime utcNow, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(16);
        var created = utcNow.ToString("o");
        var digest = Convert.ToBase64String(
            SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray()));
        return $"UsernameToken Username=\"{username}\", PasswordDigest=\"{digest}\", " +
               $"Nonce=\"{Convert.ToBase64String(nonce)}\", Created=\"{created}\"";
    }

    /// <summary>Handler credentials for NTLM, or null for every other auth type.</summary>
    public static NetworkCredential? NtlmCredential(AuthConfig? auth, IReadOnlyDictionary<string, string> vars)
    {
        if (auth?.Type != AuthType.Ntlm) return null;
        var username = Get(auth, "username", vars);
        if (string.IsNullOrEmpty(username)) return null;
        var domain = Get(auth, "domain", vars);
        var password = Get(auth, "password", vars);
        return string.IsNullOrEmpty(domain)
            ? new NetworkCredential(username, password)
            : new NetworkCredential(username, password, domain);
    }

    /// <summary>For Digest auth: parses the <c>WWW-Authenticate</c> challenge on a 401 and
    /// returns the <c>Authorization</c> value for the retry, or null when there's nothing to
    /// retry (not Digest, not a 401, no Digest challenge, or no username).</summary>
    public static string? DigestRetryHeader(
        AuthConfig? auth,
        HttpExecutionResult challengeResponse,
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> vars)
    {
        if (auth?.Type != AuthType.Digest || challengeResponse.StatusCode != 401) return null;
        var username = Get(auth, "username", vars);
        if (string.IsNullOrEmpty(username)) return null;

        // RFC 7235 allows several WWW-Authenticate headers; use the first Digest one.
        foreach (var (name, value) in challengeResponse.Headers)
        {
            if (!string.Equals(name, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)) continue;
            if (DigestAuthenticator.TryParseChallenge(value, out var challenge) && challenge is not null)
            {
                return DigestAuthenticator.BuildAuthorizationHeader(
                    challenge, method, url.PathAndQuery, username, Get(auth, "password", vars)).Value;
            }
        }
        return null;
    }

    private static string Get(AuthConfig auth, string key, IReadOnlyDictionary<string, string> vars) =>
        auth.Parameters.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
            ? Interpolator.Resolve(v, vars)
            : string.Empty;

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
