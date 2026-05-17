using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vegha.Core.Requests;

/// <summary>
/// Base64URL-decodes a JWT (header.payload.signature) and exposes the payload as a
/// pretty-printed JSON string plus a small set of well-known claims (aud / iss / exp /
/// sub / iat / nbf / client_id). No signature validation — this is purely informational,
/// surfaced under "Decoded Payload" in the OAuth2 auth panel. Matches Bruno/Postman.
/// </summary>
public static class JwtDecoder
{
    /// <summary>Returns true when <paramref name="token"/> looks like a JWT
    /// (three base64url segments separated by dots). Used to gate the decoded view in the UI.</summary>
    public static bool LooksLikeJwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var dots = token.Count(c => c == '.');
        return dots == 2;
    }

    /// <summary>Pretty-prints the JWT payload as indented JSON. Returns <see cref="string.Empty"/>
    /// when the token isn't a JWT or the payload can't be parsed (which is fine — the UI just
    /// hides the decoded section).</summary>
    public static string PrettyPrintPayload(string? token)
    {
        var json = TryDecodePayloadJson(token);
        if (string.IsNullOrEmpty(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            // Fall back to the raw decoded JSON if pretty-printing fails — better to show
            // ugly JSON than an empty box.
            return json;
        }
    }

    /// <summary>Returns the JWT payload as raw JSON (compact, no whitespace adjustment).
    /// Null when the token isn't a JWT or the segment doesn't decode to text.</summary>
    public static string? TryDecodePayloadJson(string? token)
    {
        if (!LooksLikeJwt(token)) return null;
        var parts = token!.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var bytes = Base64UrlDecode(parts[1]);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the decoded JWT header as raw JSON. Useful for surfacing
    /// algorithm (<c>alg</c>) and key id (<c>kid</c>) in the UI.</summary>
    public static string? TryDecodeHeaderJson(string? token)
    {
        if (!LooksLikeJwt(token)) return null;
        var parts = token!.Split('.');
        if (parts.Length < 1) return null;
        try
        {
            var bytes = Base64UrlDecode(parts[0]);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the standard claims used by the UI's decoded-payload section.
    /// Returns null when the token isn't a parseable JWT.</summary>
    public static JwtClaims? TryExtractClaims(string? token)
    {
        var json = TryDecodePayloadJson(token);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new JwtClaims(
                Audience: StringOrJoin(root, "aud"),
                Issuer: StringOrNull(root, "iss"),
                Subject: StringOrNull(root, "sub"),
                ClientId: StringOrNull(root, "client_id"),
                ApplicationName: StringOrNull(root, "application_name"),
                IssuedAt: NumberToTime(root, "iat"),
                NotBefore: NumberToTime(root, "nbf"),
                Expiry: NumberToTime(root, "exp"));
        }
        catch
        {
            return null;
        }
    }

    // ----- helpers -----

    private static byte[] Base64UrlDecode(string input)
    {
        // base64url -> base64: replace - / _ with + /  and right-pad to a multiple of 4.
        var b64 = input.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(b64);
    }

    private static string? StringOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    /// <summary>JWT <c>aud</c> may be either a string or an array of strings. Normalize.</summary>
    private static string? StringOrJoin(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            return string.Join(", ",
                v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
        return null;
    }

    private static DateTime? NumberToTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        long? seconds = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var ns) => ns,
            _ => null
        };
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
    }
}

/// <summary>Subset of JWT registered claims surfaced in the UI's Decoded Payload section.
/// All fields are optional — IdPs vary on which are present.</summary>
public sealed record JwtClaims(
    string? Audience,
    string? Issuer,
    string? Subject,
    string? ClientId,
    string? ApplicationName,
    DateTime? IssuedAt,
    DateTime? NotBefore,
    DateTime? Expiry);
