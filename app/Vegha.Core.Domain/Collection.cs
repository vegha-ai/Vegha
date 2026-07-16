namespace Vegha.Core.Domain;

/// <summary>Top-level collection — the root of a folder of requests.</summary>
public sealed record Collection
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0";
    public IList<KvPair> Variables { get; init; } = new List<KvPair>();
    public IList<Environment> Environments { get; init; } = new List<Environment>();
    public IList<RequestItem> Requests { get; init; } = new List<RequestItem>();
    public IList<Folder> Folders { get; init; } = new List<Folder>();
    public AuthConfig? Auth { get; init; }

    /// <summary>Headers applied to every request in the collection (request-level same-key
    /// entries override). Bruno parity.</summary>
    public IList<KvPair> Headers { get; init; } = new List<KvPair>();
    /// <summary>Pre-request script that runs before every request in the collection. Concatenated
    /// with folder + request scripts at execution time (collection runs first).</summary>
    public string? PreRequestScript { get; init; }
    /// <summary>Post-response script that runs after the response but before tests. Concatenated
    /// with folder + request scripts. Bruno parity (<c>script:post-response { }</c> block).</summary>
    public string? PostResponseScript { get; init; }
    /// <summary>Tests script that runs after every request. Concatenated with folder + request scripts.</summary>
    public string? TestsScript { get; init; }
    /// <summary>Markdown docs displayed at the collection level.</summary>
    public string? Docs { get; init; }
    /// <summary>Defaults applied when creating a NEW request in this collection (request type
    /// + base URL). Bruno's "Presets" collection setting. Null when no presets are configured —
    /// new requests fall back to HTTP / empty URL. Persisted as the <c>presets { }</c> block
    /// in <c>collection.bru</c>.</summary>
    public RequestPresets? Presets { get; init; }
}

/// <summary>Per-collection defaults for new requests (Bruno's "Presets" settings tab).
/// <see cref="RequestType"/> is one of "http" / "graphql" / "grpc" / "websocket" — the same
/// tokens the New Request dialog uses. <see cref="BaseUrl"/> pre-fills the URL field.</summary>
public sealed record RequestPresets
{
    public string RequestType { get; init; } = "http";
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>True when neither a non-default type nor a URL is set — such presets are
    /// equivalent to "no presets" and don't need to be emitted.</summary>
    public bool IsEmpty =>
        (string.IsNullOrEmpty(RequestType) || RequestType == "http")
        && string.IsNullOrEmpty(BaseUrl);
}

/// <summary>A named set of variables (e.g. "Local", "Prod") loaded from <c>environments/*.bru</c>.</summary>
public sealed record Environment
{
    /// <summary>Stable identity (Guid string). Persisted in <c>.env.json</c> so the env survives
    /// renames without losing the user's "active env" selection. Generated lazily by the
    /// loader when an existing file lacks an id (back-compat with pre-Id env files).</summary>
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IList<KvPair> Variables { get; init; } = new List<KvPair>();
    /// <summary>Names of variables flagged as secret (their values may be redacted in UI).</summary>
    public IList<string> SecretVariables { get; init; } = new List<string>();
    /// <summary>Optional hex color (e.g. <c>"#10B981"</c>) used by the UI to render this env as
    /// a colored pill / dot. Null means "no color set" — UI falls back to neutral styling.</summary>
    public string? Color { get; init; }
}

/// <summary>Group of requests inside a collection. Folders may nest.</summary>
public sealed record Folder
{
    public string Name { get; init; } = string.Empty;
    public IList<RequestItem> Requests { get; init; } = new List<RequestItem>();
    public IList<Folder> Folders { get; init; } = new List<Folder>();
    public AuthConfig? Auth { get; init; }

    /// <summary>Folder-level vars merged with collection vars at execution time; request vars override.</summary>
    public IList<KvPair> Variables { get; init; } = new List<KvPair>();
    /// <summary>Folder-level headers merged with collection + request headers (last-wins).</summary>
    public IList<KvPair> Headers { get; init; } = new List<KvPair>();
    /// <summary>Pre-request script for every request in this folder. Runs after collection's, before request's.</summary>
    public string? PreRequestScript { get; init; }
    /// <summary>Post-response script for every request in this folder. Runs after collection's,
    /// before request's, before tests. Bruno parity (<c>script:post-response { }</c> block).</summary>
    public string? PostResponseScript { get; init; }
    /// <summary>Tests script for every request in this folder.</summary>
    public string? TestsScript { get; init; }
    /// <summary>Markdown docs displayed at the folder level.</summary>
    public string? Docs { get; init; }
}

/// <summary>A single request within a collection or folder.</summary>
public sealed record RequestItem
{
    public string Name { get; init; } = string.Empty;
    public RequestKind Kind { get; init; } = RequestKind.Http;
    /// <summary>Raw <c>meta.type</c> string from the .bru file. Preserves the original even
    /// when <see cref="Kind"/> normalizes to <c>RequestKind.Http</c> (e.g. <c>type: soap</c>
    /// files are edited in the HTTP workspace but should re-emit as <c>type: soap</c> on
    /// save). Null when the source file had no explicit type — emitter falls back to Kind.</summary>
    public string? MetaType { get; init; }
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public IList<KvPair> Params { get; init; } = new List<KvPair>();
    public IList<KvPair> PathParams { get; init; } = new List<KvPair>();
    public IList<KvPair> Headers { get; init; } = new List<KvPair>();
    public BodyConfig Body { get; init; } = new();
    public AuthConfig? Auth { get; init; }
    public IList<KvPair> PreRequestVars { get; init; } = new List<KvPair>();
    public IList<KvPair> PostResponseVars { get; init; } = new List<KvPair>();
    public string? PreRequestScript { get; init; }
    /// <summary>Post-response script — runs after the response, before tests. Bruno parity
    /// (<c>script:post-response { }</c>). Distinct from <see cref="Tests"/>: post-response is
    /// for side-effects (var extraction, header inspection); tests is for assertions.</summary>
    public string? PostResponseScript { get; init; }
    public string? Tests { get; init; }
    public string? Docs { get; init; }
    public RequestSettingsConfig Settings { get; init; } = new();

    /// <summary>SOAP-specific outgoing configuration — WS-Security and WS-Addressing headers
    /// generated into the SOAP <c>&lt;Header&gt;</c> at send time. Null for non-SOAP requests
    /// and for SOAP requests with no WS-* configuration. Persisted as the <c>soap { }</c> block.</summary>
    public SoapConfig? Soap { get; init; }

    /// <summary>Absolute path to the <c>.bru</c> file this request was loaded from. Set by
    /// <c>CollectionLoader</c> at load time so the UI can resolve the on-disk file without
    /// re-reading and re-parsing every sibling <c>.bru</c> to recover it. Transient: it is
    /// not part of the serialized <c>.bru</c> format (emitters ignore it) and is recomputed on
    /// each load. Null for in-memory requests that aren't backed by a file yet (e.g. a fresh
    /// cURL import before it's persisted).</summary>
    public string? SourcePath { get; init; }
}

/// <summary>SOAP WS-* configuration applied to the outgoing envelope at send time. Each
/// section is independent and optional; a null section means "don't emit that header".
/// Mirrors the per-request WS-Security a SoapUI project carries.</summary>
public sealed record SoapConfig
{
    /// <summary>WS-Security <c>&lt;wsu:Timestamp&gt;</c> — when set, a fresh Created/Expires
    /// pair is generated on every send.</summary>
    public WssTimestampConfig? Timestamp { get; init; }
    /// <summary>WS-Security <c>&lt;wsse:UsernameToken&gt;</c> inside the <c>&lt;wsse:Security&gt;</c> header.</summary>
    public WssUsernameTokenConfig? UsernameToken { get; init; }
    /// <summary>WS-Addressing headers (<c>wsa:Action</c>, <c>wsa:To</c>, …) in the SOAP header.</summary>
    public WsAddressingConfig? Addressing { get; init; }
}

/// <summary>WS-Security Timestamp. <see cref="TimeToLiveSeconds"/> is SoapUI's
/// <c>wss-time-to-live</c> — the gap between <c>Created</c> and <c>Expires</c>.</summary>
public sealed record WssTimestampConfig
{
    public int TimeToLiveSeconds { get; init; } = 60;
}

/// <summary>WS-Security UsernameToken. Password is sent as plaintext (<see cref="WssPasswordType.Text"/>)
/// or as a SHA-1 digest of nonce + created + password (<see cref="WssPasswordType.Digest"/>).</summary>
public sealed record WssUsernameTokenConfig
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public WssPasswordType PasswordType { get; init; } = WssPasswordType.Text;
    public bool AddNonce { get; init; } = true;
    public bool AddCreated { get; init; } = true;
}

public enum WssPasswordType
{
    Text,
    Digest,
}

/// <summary>WS-Addressing headers. An empty <see cref="MessageId"/> with
/// <see cref="AutoMessageId"/> set emits a fresh <c>urn:uuid:</c> on every send.</summary>
public sealed record WsAddressingConfig
{
    public string? Action { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? MessageId { get; init; }
    public bool AutoMessageId { get; init; } = true;
}

/// <summary>Per-request transport tweaks (the <c>settings { }</c> block in .bru).</summary>
public sealed record RequestSettingsConfig
{
    public bool FollowRedirects { get; init; } = true;
    public bool VerifySsl { get; init; } = true;
    public bool EncodeUrl { get; init; } = true;
    public bool SendCookies { get; init; } = true;
    public bool SaveCookies { get; init; } = true;
    public bool EnableHttp2 { get; init; } = false;
    /// <summary>Path to an mTLS client certificate: .pfx/.p12 (PKCS#12) or .pem
    /// (cert+key). Supports {{var}} interpolation at execution time.</summary>
    public string? MtlsCertPath { get; init; }
    /// <summary>Password for a PKCS#12 client certificate. Supports {{var}}
    /// interpolation so secrets can live in environments instead of the file.</summary>
    public string? MtlsCertPassword { get; init; }
}

public enum RequestKind
{
    Http,
    GraphQL,
    Grpc,
    WebSocket,
    Soap
}

/// <summary>One key/value entry: a query param, header, env var, etc.</summary>
public sealed record KvPair
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }

    public KvPair() { }
    public KvPair(string name, string value, bool enabled = true)
    {
        Name = name;
        Value = value;
        Enabled = enabled;
    }
}

/// <summary>Body content + type. Mode "none" means no body.</summary>
public sealed record BodyConfig
{
    public BodyMode Mode { get; init; } = BodyMode.None;
    public string? Content { get; init; }
    /// <summary>Key/value pairs for <see cref="BodyMode.FormUrlEncoded"/>. Multipart-form
    /// uses the richer <see cref="MultipartItems"/> list so file rows and per-part content
    /// types can be modeled cleanly.</summary>
    public IList<KvPair> FormData { get; init; } = new List<KvPair>();
    /// <summary>Field rows for <see cref="BodyMode.MultipartForm"/>. Each row can be a text
    /// part or a file part with optional content-type override. Bruno parity.</summary>
    public IList<MultipartFormItem> MultipartItems { get; init; } = new List<MultipartFormItem>();
    /// <summary>Absolute path on disk for <see cref="BodyMode.Binary"/> file uploads.
    /// When set, the executor streams the file as the request body.</summary>
    public string? FilePath { get; init; }
    /// <summary>Optional content-type override for the file body (e.g. <c>image/png</c>).
    /// When empty the executor falls back to the file extension's MIME type.</summary>
    public string? FileContentType { get; init; }
    public string? GraphQLQuery { get; init; }
    public string? GraphQLVariables { get; init; }
}

/// <summary>One field row inside a multipart-form body. <see cref="Kind"/> distinguishes
/// text rows (Value is the literal field value) from file rows (Value is an absolute or
/// collection-relative file path). <see cref="ContentType"/> overrides the auto-detected
/// MIME type for the part; null falls back to the file extension's guess or
/// <c>text/plain</c> for text rows.</summary>
public sealed record MultipartFormItem
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    /// <summary>"text" (default) or "file".</summary>
    public string Kind { get; init; } = "text";
    public string? ContentType { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
}

public enum BodyMode
{
    None,
    Json,
    Text,
    Xml,
    Sparql,
    GraphQL,
    FormUrlEncoded,
    MultipartForm,
    Binary
}

/// <summary>Authentication configuration. Type "inherit" defers to parent folder/collection.</summary>
public sealed record AuthConfig
{
    public AuthType Type { get; init; } = AuthType.None;
    public IDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public enum AuthType
{
    None,
    Inherit,
    ApiKey,
    Bearer,
    Basic,
    Digest,
    OAuth1,
    OAuth2,
    AwsV4,
    Ntlm,
    Wsse
}
