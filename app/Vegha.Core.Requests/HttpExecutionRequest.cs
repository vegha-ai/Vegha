namespace Vegha.Core.Requests;

/// <summary>An HTTP request to be executed by <see cref="HttpExecutor"/>.
/// Body content has a precedence order: <see cref="FilePath"/> &gt; <see cref="MultipartFields"/>
/// &gt; <see cref="FormFields"/> &gt; <see cref="Body"/>. The first non-empty one is used; the
/// rest are ignored. Most callers set only one.</summary>
public sealed record HttpExecutionRequest(
    HttpMethod Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    string? Body = null,
    string? ContentType = null,
    /// <summary>Key/value pairs sent as <c>application/x-www-form-urlencoded</c>. Mutually
    /// exclusive with <see cref="Body"/> / <see cref="MultipartFields"/> / <see cref="FilePath"/>.</summary>
    IReadOnlyList<KeyValuePair<string, string>>? FormFields = null,
    /// <summary>Field rows for <c>multipart/form-data</c>. Each row is either a text part
    /// or a file part — distinguished by the <c>Kind</c> field of the value tuple.</summary>
    IReadOnlyList<MultipartField>? MultipartFields = null,
    /// <summary>Absolute path of a file streamed verbatim as the request body
    /// (Content-Type comes from <see cref="ContentType"/> or the file extension).</summary>
    string? FilePath = null,
    /// <summary>If null, the executor's default HttpClient is used (handler reuse).
    /// If set, a per-request handler is constructed honoring these tweaks.</summary>
    HttpRequestOptions? Options = null);

/// <summary>One row of a multipart-form body. Text rows put <c>Value</c> directly in a
/// <c>StringContent</c>; file rows treat <c>Value</c> as a file path and attach the bytes
/// via <c>StreamContent</c>. <see cref="ContentType"/> overrides the auto-detected MIME.</summary>
public sealed record MultipartField(string Name, string Value, string Kind = "text", string? ContentType = null);

/// <summary>Per-request transport overrides. <c>null</c> values mean "use default".</summary>
public sealed record HttpRequestOptions(
    bool? FollowRedirects = null,
    bool? VerifySsl = null,
    /// <summary>If false, this request neither sends nor saves cookies. Default true (uses the shared jar).</summary>
    bool? UseCookies = null,
    /// <summary>Explicit NTLM credentials for this request. When set, the executor builds a
    /// one-off handler with <c>HttpClientHandler.Credentials</c> populated and uses NTLM
    /// (no Kerberos / SPNEGO fallback).</summary>
    System.Net.NetworkCredential? NtlmCredential = null,
    /// <summary>Per-request mTLS client certificate (loaded from PFX or PEM). When set,
    /// the executor builds a one-off handler with this cert in SslOptions.ClientCertificates.</summary>
    System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate = null);
