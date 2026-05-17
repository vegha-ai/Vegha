using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>
/// AWS Signature Version 4 signer. Produces the headers AWS expects on signed requests:
/// <c>Authorization</c>, <c>X-Amz-Date</c>, <c>X-Amz-Content-SHA256</c>, optional
/// <c>X-Amz-Security-Token</c>. Algorithm reference:
/// https://docs.aws.amazon.com/general/latest/gr/signing_aws_api_requests.html
///
/// The implementation is hand-rolled (no AWSSDK.Core dependency) so it can sign arbitrary
/// HTTP requests, not just AWS-SDK-shaped ones. Validated against AWS's official test vectors
/// in the integration tests.
/// </summary>
public static class AwsV4Signer
{
    public sealed record Inputs(
        string Method,
        Uri Url,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string Body,
        string AccessKeyId,
        string SecretAccessKey,
        string Region,
        string Service,
        string? SessionToken = null,
        DateTime? RequestUtcOverride = null);

    /// <summary>The headers AWS expects on a signed request.</summary>
    public sealed record Output(
        string Authorization,
        string XAmzDate,
        string XAmzContentSha256,
        string? XAmzSecurityToken);

    private const string Algorithm = "AWS4-HMAC-SHA256";

    public static Output Sign(Inputs i)
    {
        var now = (i.RequestUtcOverride ?? DateTime.UtcNow).ToUniversalTime();
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var bodyHash = Sha256Hex(i.Body ?? string.Empty);

        // Canonical headers must include host + x-amz-date + x-amz-content-sha256 (+ session token).
        // Caller-supplied headers participate too.
        var canonicalHeaderMap = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in i.Headers ?? Array.Empty<KeyValuePair<string, string>>())
        {
            var key = name.Trim().ToLowerInvariant();
            // Skip Authorization — we're producing it. Skip x-amz-date / x-amz-content-sha256 which
            // we'll set authoritatively below.
            if (key is "authorization" or "x-amz-date" or "x-amz-content-sha256" or "x-amz-security-token")
                continue;
            canonicalHeaderMap[key] = CollapseWhitespace(value.Trim());
        }
        canonicalHeaderMap["host"] = i.Url.IsDefaultPort ? i.Url.Host : $"{i.Url.Host}:{i.Url.Port}";
        canonicalHeaderMap["x-amz-date"] = amzDate;
        canonicalHeaderMap["x-amz-content-sha256"] = bodyHash;
        if (!string.IsNullOrEmpty(i.SessionToken))
            canonicalHeaderMap["x-amz-security-token"] = i.SessionToken;

        var signedHeadersList = string.Join(";", canonicalHeaderMap.Keys);
        var canonicalHeaders = new StringBuilder();
        foreach (var kv in canonicalHeaderMap)
            canonicalHeaders.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');

        var canonicalRequest =
            i.Method.ToUpperInvariant() + "\n" +
            CanonicalUri(i.Url) + "\n" +
            CanonicalQuery(i.Url) + "\n" +
            canonicalHeaders + "\n" +
            signedHeadersList + "\n" +
            bodyHash;

        var credentialScope = $"{dateStamp}/{i.Region}/{i.Service}/aws4_request";
        var stringToSign =
            Algorithm + "\n" +
            amzDate + "\n" +
            credentialScope + "\n" +
            Sha256Hex(canonicalRequest);

        var signingKey = DeriveSigningKey(i.SecretAccessKey, dateStamp, i.Region, i.Service);
        var signature = ToHex(HmacSha256(signingKey, stringToSign));

        var authorization =
            $"{Algorithm} Credential={i.AccessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeadersList}, " +
            $"Signature={signature}";

        return new Output(
            Authorization: authorization,
            XAmzDate: amzDate,
            XAmzContentSha256: bodyHash,
            XAmzSecurityToken: string.IsNullOrEmpty(i.SessionToken) ? null : i.SessionToken);
    }

    /// <summary>Convenience overload: build Inputs from an <see cref="AuthConfig"/> with type AwsV4.</summary>
    public static Output? SignFromAuthConfig(
        AuthConfig auth,
        string method,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string body,
        IReadOnlyDictionary<string, string>? vars = null,
        DateTime? utcOverride = null)
    {
        if (auth.Type != AuthType.AwsV4) return null;

        string Get(string key) =>
            auth.Parameters.TryGetValue(key, out var v) ? Resolve(v, vars) : string.Empty;

        var accessKey = Get("accessKeyId");
        var secret    = Get("secretAccessKey");
        var region    = Get("region");
        var service   = Get("service");
        var sessionToken = Get("sessionToken");

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secret) ||
            string.IsNullOrEmpty(region) || string.IsNullOrEmpty(service))
            return null;

        return Sign(new Inputs(
            method, url, headers, body,
            accessKey, secret, region, service,
            string.IsNullOrEmpty(sessionToken) ? null : sessionToken,
            utcOverride));
    }

    // ============================== Helpers ==============================

    private static string Resolve(string s, IReadOnlyDictionary<string, string>? vars) =>
        vars is null ? s : Interpolator.Resolve(s, vars);

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return ToHex(hash);
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static byte[] DeriveSigningKey(string secret, string dateStamp, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secret);
        var kDate    = HmacSha256(kSecret, dateStamp);
        var kRegion  = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (var i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string CanonicalUri(Uri url)
    {
        var path = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
        return EncodeAwsPath(path);
    }

    /// <summary>
    /// AWS expects URI segments encoded twice for non-S3 services per the SigV4 spec, but the
    /// "test suite" canonical examples encode once. We follow the simpler once-encoded form
    /// (which matches AWS's published vectors and what most services actually accept).
    /// </summary>
    private static string EncodeAwsPath(string path)
    {
        if (path == "/") return "/";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            // Already-encoded characters in path (e.g. %20) should be preserved, not double-encoded.
            // Uri.EscapeDataString re-escapes everything; instead, escape only unreserved-violating chars.
            segments[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
        }
        return string.Join("/", segments);
    }

    private static string CanonicalQuery(Uri url)
    {
        var q = url.Query;
        if (string.IsNullOrEmpty(q) || q == "?") return string.Empty;
        if (q[0] == '?') q = q[1..];

        var pairs = new List<(string Key, string Value)>();
        foreach (var pair in q.Split('&'))
        {
            if (pair.Length == 0) continue;
            var idx = pair.IndexOf('=');
            var k = idx < 0 ? pair : pair[..idx];
            var v = idx < 0 ? string.Empty : pair[(idx + 1)..];
            pairs.Add((
                Uri.EscapeDataString(Uri.UnescapeDataString(k)),
                Uri.EscapeDataString(Uri.UnescapeDataString(v))));
        }
        pairs.Sort((a, b) =>
        {
            var byKey = string.CompareOrdinal(a.Key, b.Key);
            return byKey != 0 ? byKey : string.CompareOrdinal(a.Value, b.Value);
        });
        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    private static string CollapseWhitespace(string s)
    {
        // Replace runs of internal whitespace with a single space (per SigV4 spec for header values).
        var sb = new StringBuilder(s.Length);
        var inWs = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ' ' || c == '\t')
            {
                if (!inWs) { sb.Append(' '); inWs = true; }
            }
            else { sb.Append(c); inWs = false; }
        }
        return sb.ToString();
    }
}
