namespace Vegha.App.ViewModels;

/// <summary>
/// Static catalog of common HTTP header names + a small set of well-known values for the
/// most autocomplete-worthy headers (Content-Type, Accept, Cache-Control, …).
/// Wired into <c>KvEditor</c> when its <c>Mode="headers"</c> so the Name cell suggests
/// standard headers and the Value cell suggests common MIME types / cache directives.
///
/// Sources: RFC 7231 / 9110 (semantics), IANA Message Headers Registry. Curated to
/// what API testing typically needs — not exhaustive.
/// </summary>
public static class HttpHeaderCatalog
{
    /// <summary>Common HTTP request header names (alphabetical). Used as the autocomplete
    /// source for the Headers tab's Name column.</summary>
    public static readonly string[] RequestHeaderNames =
    {
        "Accept",
        "Accept-Charset",
        "Accept-Encoding",
        "Accept-Language",
        "Authorization",
        "Cache-Control",
        "Connection",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Length",
        "Content-Location",
        "Content-Range",
        "Content-Type",
        "Cookie",
        "DNT",
        "Date",
        "ETag",
        "Expect",
        "Forwarded",
        "From",
        "Host",
        "If-Match",
        "If-Modified-Since",
        "If-None-Match",
        "If-Range",
        "If-Unmodified-Since",
        "Origin",
        "Pragma",
        "Range",
        "Referer",
        "TE",
        "Upgrade",
        "Upgrade-Insecure-Requests",
        "User-Agent",
        "Via",
        "Warning",
        "X-Api-Key",
        "X-Correlation-Id",
        "X-CSRF-Token",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-HTTP-Method-Override",
        "X-Real-IP",
        "X-Request-Id",
        "X-Requested-With",
    };

    /// <summary>Known values for headers that have a small enumerable set worth surfacing
    /// as autocomplete suggestions. Lookup is case-insensitive on the header name. Returns
    /// null when the header isn't in the catalog — caller falls back to plain text entry.</summary>
    public static IReadOnlyList<string>? SuggestedValuesFor(string? headerName)
    {
        if (string.IsNullOrEmpty(headerName)) return null;
        return headerName.ToLowerInvariant() switch
        {
            "content-type" or "accept" => MimeTypes,
            "accept-encoding" or "content-encoding" => Encodings,
            "cache-control"     => CacheControls,
            "connection"        => Connections,
            "accept-charset"    => Charsets,
            "accept-language"   => Languages,
            "x-http-method-override" => HttpMethods,
            "x-requested-with"  => new[] { "XMLHttpRequest" },
            "upgrade-insecure-requests" => new[] { "1" },
            "dnt"               => new[] { "0", "1" },
            "expect"            => new[] { "100-continue" },
            _ => null
        };
    }

    private static readonly string[] MimeTypes =
    {
        "application/json",
        "application/xml",
        "application/x-www-form-urlencoded",
        "multipart/form-data",
        "application/octet-stream",
        "application/pdf",
        "application/javascript",
        "application/graphql",
        "application/grpc",
        "application/soap+xml",
        "text/plain",
        "text/html",
        "text/csv",
        "text/xml",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/svg+xml",
        "image/webp",
        "*/*",
    };

    private static readonly string[] Encodings =
    {
        "gzip", "deflate", "br", "identity", "*",
        "gzip, deflate", "gzip, deflate, br"
    };

    private static readonly string[] CacheControls =
    {
        "no-cache", "no-store", "no-transform", "only-if-cached",
        "max-age=0", "max-age=3600",
        "max-stale", "min-fresh=60",
        "private", "public", "must-revalidate", "proxy-revalidate", "immutable"
    };

    private static readonly string[] Connections = { "keep-alive", "close", "upgrade" };

    private static readonly string[] Charsets = { "utf-8", "iso-8859-1", "utf-16", "*" };

    private static readonly string[] Languages =
    {
        "en-US", "en", "en-GB", "es", "fr", "de", "ja", "zh-CN", "*"
    };

    private static readonly string[] HttpMethods =
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };
}
