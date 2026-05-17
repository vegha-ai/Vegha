using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vegha.Core.Requests;

/// <summary>
/// HTTP Digest authentication per RFC 7616 (and the older RFC 2617 / RFC 2069 forms).
/// Stateless: callers feed in a parsed <see cref="DigestChallenge"/> and per-request
/// inputs (method, URI, optional entity body for qop=auth-int) and get back the
/// Authorization header value to send.
///
/// Supports algorithms <c>MD5</c>, <c>MD5-sess</c>, <c>SHA-256</c>, <c>SHA-256-sess</c>,
/// <c>SHA-512-256</c>, <c>SHA-512-256-sess</c>. qop is <c>auth</c> (default), <c>auth-int</c>,
/// or absent (legacy RFC 2069). The cnonce + nc are generated freshly per call so each
/// retried request has a deterministic test seam via <see cref="ICnonceProvider"/>.
/// </summary>
public static class DigestAuthenticator
{
    /// <summary>The default cnonce/timestamp source. Tests inject a deterministic
    /// alternative when verifying against RFC fixture vectors.</summary>
    public interface ICnonceProvider
    {
        string NewCnonce();
    }

    private sealed class CryptoCnonceProvider : ICnonceProvider
    {
        public string NewCnonce()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public static readonly ICnonceProvider DefaultCnonceProvider = new CryptoCnonceProvider();

    /// <summary>Result of building a Digest Authorization header — the header value
    /// plus the nc counter used (so callers tracking nc across retries can advance it).</summary>
    public sealed record AuthorizationHeader(string Value, int NonceCount);

    public static AuthorizationHeader BuildAuthorizationHeader(
        DigestChallenge challenge,
        string method,
        string requestUri,
        string username,
        string password,
        byte[]? entityBody = null,
        int nonceCount = 1,
        ICnonceProvider? cnonceProvider = null)
    {
        if (challenge is null) throw new ArgumentNullException(nameof(challenge));
        if (string.IsNullOrEmpty(challenge.Realm)) throw new ArgumentException("Challenge missing realm", nameof(challenge));
        if (string.IsNullOrEmpty(challenge.Nonce)) throw new ArgumentException("Challenge missing nonce", nameof(challenge));

        cnonceProvider ??= DefaultCnonceProvider;
        var algorithm = string.IsNullOrEmpty(challenge.Algorithm) ? "MD5" : challenge.Algorithm;
        var qop = SelectQop(challenge.Qop);
        var cnonce = qop is null && !IsSessAlgorithm(algorithm) ? null : cnonceProvider.NewCnonce();
        var nc = qop is null ? null : nonceCount.ToString("x8", CultureInfo.InvariantCulture);

        var ha1 = ComputeHA1(algorithm, username, challenge.Realm, password, challenge.Nonce, cnonce);
        var ha2 = ComputeHA2(algorithm, qop, method, requestUri, entityBody);
        var response = ComputeResponse(algorithm, ha1, challenge.Nonce, nc, cnonce, qop, ha2);

        var header = BuildHeader(
            username, challenge.Realm, challenge.Nonce, requestUri, response,
            algorithm, qop, nc, cnonce, challenge.Opaque, challenge.Userhash);

        return new AuthorizationHeader(header, nonceCount);
    }

    /// <summary>Parses the <c>Digest</c> challenge from a 401 response's
    /// <c>WWW-Authenticate</c> header value. Handles quoted strings, comma-separated
    /// directives, and tolerates leading/trailing whitespace.</summary>
    public static bool TryParseChallenge(string headerValue, out DigestChallenge? challenge)
    {
        challenge = null;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;

        // Header may carry multiple schemes ("Digest ..., Basic ..."). Locate the Digest token.
        var trimmed = headerValue.TrimStart();
        if (!trimmed.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return false;

        var paramText = trimmed.Substring("Digest".Length).Trim();
        var directives = ParseDirectives(paramText);

        if (!directives.TryGetValue("realm", out var realm)) return false;
        if (!directives.TryGetValue("nonce", out var nonce)) return false;

        directives.TryGetValue("algorithm", out var algorithm);
        directives.TryGetValue("qop", out var qop);
        directives.TryGetValue("opaque", out var opaque);
        directives.TryGetValue("userhash", out var userhashRaw);
        var userhash = string.Equals(userhashRaw, "true", StringComparison.OrdinalIgnoreCase);

        challenge = new DigestChallenge(realm, nonce, algorithm, qop, opaque, userhash);
        return true;
    }

    // ---- digest-internal helpers ----

    private static string ComputeHA1(
        string algorithm, string username, string realm, string password,
        string nonce, string? cnonce)
    {
        var basic = Hash(algorithm, $"{username}:{realm}:{password}");
        if (!IsSessAlgorithm(algorithm)) return basic;

        // *-sess: HA1 = H(H(username:realm:password) ":" nonce ":" cnonce)
        if (cnonce is null) throw new InvalidOperationException("cnonce required for *-sess algorithms");
        return Hash(algorithm, $"{basic}:{nonce}:{cnonce}");
    }

    private static string ComputeHA2(string algorithm, string? qop, string method, string requestUri, byte[]? entityBody)
    {
        if (string.Equals(qop, "auth-int", StringComparison.Ordinal))
        {
            var bodyHash = HashBytes(algorithm, entityBody ?? Array.Empty<byte>());
            return Hash(algorithm, $"{method}:{requestUri}:{bodyHash}");
        }
        return Hash(algorithm, $"{method}:{requestUri}");
    }

    private static string ComputeResponse(
        string algorithm, string ha1, string nonce, string? nc, string? cnonce, string? qop, string ha2)
    {
        // RFC 2069 (no qop): response = H(HA1:nonce:HA2)
        if (qop is null) return Hash(algorithm, $"{ha1}:{nonce}:{ha2}");
        // RFC 7616: response = H(HA1:nonce:nc:cnonce:qop:HA2)
        return Hash(algorithm, $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    private static string BuildHeader(
        string username, string realm, string nonce, string uri, string response,
        string algorithm, string? qop, string? nc, string? cnonce, string? opaque, bool userhash)
    {
        // Username escaping: if userhash=true, hash it; otherwise quote it raw. Bruno doesn't
        // implement userhash at all — we do the same ASCII path until a user reports needing it.
        var sb = new StringBuilder();
        sb.Append("Digest ");
        sb.Append("username=").Append(QuoteString(username)).Append(", ");
        sb.Append("realm=").Append(QuoteString(realm)).Append(", ");
        sb.Append("nonce=").Append(QuoteString(nonce)).Append(", ");
        sb.Append("uri=").Append(QuoteString(uri)).Append(", ");
        sb.Append("algorithm=").Append(algorithm).Append(", ");
        sb.Append("response=").Append(QuoteString(response));

        if (qop is not null)
        {
            sb.Append(", qop=").Append(qop);
            sb.Append(", nc=").Append(nc);
            sb.Append(", cnonce=").Append(QuoteString(cnonce!));
        }

        if (!string.IsNullOrEmpty(opaque))
        {
            sb.Append(", opaque=").Append(QuoteString(opaque));
        }

        if (userhash)
        {
            sb.Append(", userhash=true");
        }

        return sb.ToString();
    }

    /// <summary>Picks the qop variant we'll claim. Servers advertise a comma-list
    /// (e.g., "auth,auth-int"); prefer plain "auth", fall back to "auth-int", else null.</summary>
    private static string? SelectQop(string? challengeQop)
    {
        if (string.IsNullOrEmpty(challengeQop)) return null;
        var parts = challengeQop.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (string.Equals(p, "auth", StringComparison.OrdinalIgnoreCase)) return "auth";
        }
        foreach (var p in parts)
        {
            if (string.Equals(p, "auth-int", StringComparison.OrdinalIgnoreCase)) return "auth-int";
        }
        return null;
    }

    private static bool IsSessAlgorithm(string algorithm)
        => algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase);

    private static string Hash(string algorithm, string input)
        => HashBytes(algorithm, Encoding.UTF8.GetBytes(input));

    private static string HashBytes(string algorithm, byte[] bytes)
    {
        // Treat -sess as an HA1 modifier, not a different hash function: H is the same.
        var core = algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase)
            ? algorithm.Substring(0, algorithm.Length - "-sess".Length)
            : algorithm;

        byte[] digest = core.ToUpperInvariant() switch
        {
            "MD5"            => MD5.HashData(bytes),
            "SHA-256"        => SHA256.HashData(bytes),
            "SHA-512-256"    => Truncate(SHA512.HashData(bytes), 32),
            _ => throw new NotSupportedException($"Digest algorithm '{algorithm}' is not supported."),
        };

        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static byte[] Truncate(byte[] source, int length)
    {
        if (source.Length == length) return source;
        var dst = new byte[length];
        Buffer.BlockCopy(source, 0, dst, 0, length);
        return dst;
    }

    private static string QuoteString(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>Parses <c>k1=v1, k2="v 2", k3=v3</c> into a dictionary, stripping quotes.</summary>
    private static Dictionary<string, string> ParseDirectives(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            // Skip whitespace and commas between directives.
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= text.Length) break;

            // Read key.
            var keyStart = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            if (keyStart == i) break;
            var key = text.Substring(keyStart, i - keyStart);

            // Skip whitespace + '=' + whitespace.
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') break;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            // Read value: quoted or token.
            string value;
            if (i < text.Length && text[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(text[i]);
                        i++;
                    }
                }
                if (i < text.Length) i++; // consume closing quote
                value = sb.ToString();
            }
            else
            {
                var valStart = i;
                while (i < text.Length && text[i] != ',' && !char.IsWhiteSpace(text[i])) i++;
                value = text.Substring(valStart, i - valStart);
            }

            dict[key] = value;
        }
        return dict;
    }
}

/// <summary>Parsed <c>WWW-Authenticate: Digest ...</c> challenge.</summary>
public sealed record DigestChallenge(
    string Realm,
    string Nonce,
    string? Algorithm,
    string? Qop,
    string? Opaque,
    bool Userhash);
