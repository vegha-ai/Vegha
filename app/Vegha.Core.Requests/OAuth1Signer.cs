using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Vegha.Core.Requests;

/// <summary>
/// OAuth 1.0a request signer per RFC 5849. Supports HMAC-SHA1 / HMAC-SHA256 / HMAC-SHA512
/// and PLAINTEXT signature methods. RSA-* methods are out of scope for this first cut —
/// they need a private-key file on disk; we'll add them when a fixture demands it.
///
/// Returns the value for the <c>Authorization: OAuth ...</c> header. Callers are
/// responsible for putting the URL + method + body params in the right shape; this
/// class doesn't sniff content-types.
/// </summary>
public static class OAuth1Signer
{
    public sealed record Config(
        string ConsumerKey,
        string ConsumerSecret,
        string SignatureMethod = "HMAC-SHA1",  // HMAC-SHA1 / HMAC-SHA256 / HMAC-SHA512 / PLAINTEXT
        string? Token = null,
        string? TokenSecret = null,
        string? Realm = null,
        string Version = "1.0",
        string? Nonce = null,                  // null → randomly generated
        long? Timestamp = null);                // null → DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    /// <summary>Builds the <c>Authorization: OAuth ...</c> header value for a request.</summary>
    public static string BuildAuthorizationHeader(
        Config config,
        string httpMethod,
        string requestUrl,
        IEnumerable<KeyValuePair<string, string>>? bodyParams = null)
    {
        var nonce = config.Nonce ?? GenerateNonce();
        var timestamp = (config.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();

        var oauthParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = config.ConsumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = config.SignatureMethod,
            ["oauth_timestamp"] = timestamp,
            ["oauth_version"] = config.Version,
        };
        if (!string.IsNullOrEmpty(config.Token))
            oauthParams["oauth_token"] = config.Token;

        var signature = ComputeSignature(config, httpMethod, requestUrl, oauthParams, bodyParams);
        oauthParams["oauth_signature"] = signature;

        var sb = new StringBuilder("OAuth ");
        if (!string.IsNullOrEmpty(config.Realm))
            sb.Append($"realm=\"{PercentEncode(config.Realm)}\", ");
        var i = 0;
        foreach (var (k, v) in oauthParams)
        {
            if (i++ > 0) sb.Append(", ");
            sb.Append(k).Append("=\"").Append(PercentEncode(v)).Append('"');
        }
        return sb.ToString();
    }

    private static string ComputeSignature(
        Config config,
        string httpMethod,
        string requestUrl,
        IDictionary<string, string> oauthParams,
        IEnumerable<KeyValuePair<string, string>>? bodyParams)
    {
        if (string.Equals(config.SignatureMethod, "PLAINTEXT", StringComparison.Ordinal))
        {
            return PercentEncode(config.ConsumerSecret) + "&" + PercentEncode(config.TokenSecret ?? string.Empty);
        }

        var baseString = BuildSignatureBaseString(httpMethod, requestUrl, oauthParams, bodyParams);
        var signingKey = PercentEncode(config.ConsumerSecret) + "&" + PercentEncode(config.TokenSecret ?? string.Empty);

        byte[] hash = config.SignatureMethod.ToUpperInvariant() switch
        {
            "HMAC-SHA1" => HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString)),
            "HMAC-SHA256" => HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString)),
            "HMAC-SHA512" => HMACSHA512.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString)),
            _ => throw new NotSupportedException($"OAuth1 signature method '{config.SignatureMethod}' not supported."),
        };
        return Convert.ToBase64String(hash);
    }

    /// <summary>RFC 5849 §3.4.1 signature base string: METHOD&amp;URL&amp;params.</summary>
    public static string BuildSignatureBaseString(
        string httpMethod,
        string requestUrl,
        IDictionary<string, string> oauthParams,
        IEnumerable<KeyValuePair<string, string>>? bodyParams)
    {
        // Normalize the URL: scheme + host + port + path; query removed and merged into params.
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException($"OAuth1: URL must be absolute, got '{requestUrl}'");

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        var baseUrl = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath}";

        // Collect every parameter (oauth + query + body) into a flat list, percent-encode
        // each, sort by encoded key (then encoded value), join with &.
        var allParams = new List<KeyValuePair<string, string>>();
        foreach (var kv in oauthParams) allParams.Add(new(kv.Key, kv.Value));

        // Query params from URL.
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var qs = HttpUtility.ParseQueryString(uri.Query);
            foreach (string? key in qs)
            {
                if (key is null) continue;
                var values = qs.GetValues(key);
                if (values is null) continue;
                foreach (var v in values) allParams.Add(new(key, v));
            }
        }

        if (bodyParams is not null)
        {
            foreach (var kv in bodyParams) allParams.Add(new(kv.Key, kv.Value));
        }

        // Encode + sort + concat.
        var encoded = allParams
            .Select(kv => new KeyValuePair<string, string>(PercentEncode(kv.Key), PercentEncode(kv.Value)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ThenBy(kv => kv.Value, StringComparer.Ordinal)
            .ToList();

        var paramString = string.Join("&", encoded.Select(kv => kv.Key + "=" + kv.Value));

        return PercentEncode(httpMethod.ToUpperInvariant()) + "&" + PercentEncode(baseUrl) + "&" + PercentEncode(paramString);
    }

    /// <summary>RFC 5849 §3.6: percent-encode unreserved chars only — strict version
    /// of what Uri.EscapeDataString would do. !*'() must be encoded.</summary>
    public static string PercentEncode(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return Uri.EscapeDataString(input)
            .Replace("!", "%21")
            .Replace("*", "%2A")
            .Replace("'", "%27")
            .Replace("(", "%28")
            .Replace(")", "%29");
    }

    private static string GenerateNonce()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(32);
        for (var i = 0; i < bytes.Length; i++) sb.Append(chars[bytes[i] % chars.Length]);
        return sb.ToString();
    }
}
