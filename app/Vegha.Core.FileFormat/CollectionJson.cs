using System.Text.Json;
using System.Text.Json.Serialization;
using Vegha.Core.Domain;

namespace Vegha.Core.FileFormat;

/// <summary>
/// JSON-native collection format. One file per request (<c>*.req.json</c>) under the
/// collection folder, plus a <c>collection.json</c> manifest with collection-level
/// auth/vars and a <c>environments/*.env.json</c> directory. This is the canonical
/// on-disk format; .bru and Postman v2 are read-only importers.
///
/// File contents are written as the corresponding DTOs in this file (CollectionFile,
/// RequestFile, EnvironmentFile) — flat JSON shapes that round-trip with the Domain
/// types via <see cref="ToCollection"/> / <see cref="FromCollection"/>.
/// </summary>
public static class CollectionJson
{
    /// <summary>Filename used for the per-collection manifest at the root of the folder.</summary>
    public const string ManifestFileName = "collection.json";

    /// <summary>Suffix for per-request files. The base name is the request name.</summary>
    public const string RequestSuffix = ".req.json";

    /// <summary>Folder under the collection root that holds environment files.</summary>
    public const string EnvironmentsFolder = "environments";

    /// <summary>Suffix for environment files. Base name is the environment name.</summary>
    public const string EnvironmentSuffix = ".env.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = CollectionJsonContext.Default,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = CollectionJsonContext.Default,
    };

    /// <summary>Normalizes the indented-JSON output to LF line endings. System.Text.Json's
    /// pretty-printer emits the platform newline (CRLF on Windows), which otherwise leaves
    /// stray <c>\r</c> in every committed file and noisy <c>^M</c> in diffs. Escaped CR/LF
    /// inside string values are unaffected — only the formatting newlines are normalized.</summary>
    private static string Lf(string json) => json.ReplaceLineEndings("\n");

    public static string SerializeManifest(CollectionFile manifest)
        => Lf(JsonSerializer.Serialize(manifest, WriteOptions));

    public static CollectionFile? DeserializeManifest(string json)
        => JsonSerializer.Deserialize<CollectionFile>(json, ReadOptions);

    public static string SerializeRequest(RequestFile req)
        => Lf(JsonSerializer.Serialize(req, WriteOptions));

    public static RequestFile? DeserializeRequest(string json)
        => JsonSerializer.Deserialize<RequestFile>(json, ReadOptions);

    public static string SerializeEnvironment(EnvironmentFile env)
        => Lf(JsonSerializer.Serialize(env, WriteOptions));

    public static EnvironmentFile? DeserializeEnvironment(string json)
        => JsonSerializer.Deserialize<EnvironmentFile>(json, ReadOptions);

    public static string SerializeFolder(FolderFile folder)
        => Lf(JsonSerializer.Serialize(folder, WriteOptions));

    public static FolderFile? DeserializeFolder(string json)
        => JsonSerializer.Deserialize<FolderFile>(json, ReadOptions);

    public const string FolderManifestFileName = "folder.json";

    /// <summary>Materialize a Domain.Collection from a manifest + per-request + per-env DTOs.</summary>
    public static Collection ToCollection(
        CollectionFile manifest,
        IEnumerable<(RequestFile request, string folderPath)> requests,
        IEnumerable<EnvironmentFile> environments)
    {
        var collection = new Collection
        {
            Name = manifest.Name,
            Version = manifest.Version,
            Variables = (manifest.Variables ?? new List<KvDto>()).Select(KvDto.ToDomain).ToList(),
            Auth = manifest.Auth?.ToDomain(),
            Environments = environments.Select(EnvironmentFile.ToDomain).ToList(),
            Headers = (manifest.Headers ?? new List<KvDto>()).Select(KvDto.ToDomain).ToList(),
            PreRequestScript = manifest.PreRequestScript,
            PostResponseScript = manifest.PostResponseScript,
            TestsScript = manifest.TestsScript,
            Docs = manifest.Docs,
        };

        // Build folder tree from each request's folderPath ("" = collection root, "a/b" = nested).
        var rootFolders = new List<Folder>();
        var rootRequests = new List<RequestItem>();
        var folderIndex = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);

        foreach (var (req, folderPath) in requests.OrderBy(t => t.folderPath, StringComparer.OrdinalIgnoreCase))
        {
            var item = req.ToDomain();
            if (string.IsNullOrEmpty(folderPath))
            {
                rootRequests.Add(item);
                continue;
            }

            var folder = EnsureFolder(rootFolders, folderIndex, folderPath);
            folder.Requests.Add(item);
        }

        return collection with { Folders = rootFolders, Requests = rootRequests };
    }

    private static Folder EnsureFolder(
        IList<Folder> rootFolders,
        IDictionary<string, Folder> index,
        string path)
    {
        if (index.TryGetValue(path, out var existing)) return existing;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = (List<Folder>)rootFolders;
        Folder? leaf = null;
        var accumulated = string.Empty;
        foreach (var part in parts)
        {
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
            if (!index.TryGetValue(accumulated, out var folder))
            {
                folder = new Folder { Name = part };
                current.Add(folder);
                index[accumulated] = folder;
            }
            leaf = folder;
            current = (List<Folder>)folder.Folders;
        }
        return leaf!;
    }

    /// <summary>Splits a Domain.Collection into a manifest + per-request DTOs (with their folder paths)
    /// + per-environment DTOs. The caller writes those out as files.</summary>
    public static (CollectionFile Manifest, List<(RequestFile Request, string FolderPath)> Requests, List<EnvironmentFile> Environments)
        FromCollection(Collection collection)
    {
        var manifest = new CollectionFile
        {
            Name = collection.Name,
            Version = collection.Version,
            Variables = collection.Variables.Count == 0 ? null : collection.Variables.Select(KvDto.FromDomain).ToList(),
            Auth = collection.Auth is null ? null : AuthDto.FromDomain(collection.Auth),
            Headers = collection.Headers.Count == 0 ? null : collection.Headers.Select(KvDto.FromDomain).ToList(),
            PreRequestScript = string.IsNullOrEmpty(collection.PreRequestScript) ? null : collection.PreRequestScript,
            PostResponseScript = string.IsNullOrEmpty(collection.PostResponseScript) ? null : collection.PostResponseScript,
            TestsScript = string.IsNullOrEmpty(collection.TestsScript) ? null : collection.TestsScript,
            Docs = string.IsNullOrEmpty(collection.Docs) ? null : collection.Docs,
        };

        var requests = new List<(RequestFile, string)>();
        foreach (var r in collection.Requests)
            requests.Add((RequestFile.FromDomain(r), string.Empty));
        WalkFolders(collection.Folders, string.Empty, requests);

        var envs = collection.Environments.Select(EnvironmentFile.FromDomain).ToList();
        return (manifest, requests, envs);
    }

    private static void WalkFolders(IList<Folder> folders, string prefix, List<(RequestFile, string)> sink)
    {
        foreach (var folder in folders)
        {
            var path = prefix.Length == 0 ? folder.Name : prefix + "/" + folder.Name;
            foreach (var r in folder.Requests)
                sink.Add((RequestFile.FromDomain(r), path));
            WalkFolders(folder.Folders, path, sink);
        }
    }
}

// ---- DTOs (the on-disk JSON shapes) ----

public sealed class CollectionFile
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public List<KvDto>? Variables { get; set; }
    public AuthDto? Auth { get; set; }
    public List<KvDto>? Headers { get; set; }
    public string? PreRequestScript { get; set; }
    public string? PostResponseScript { get; set; }
    public string? TestsScript { get; set; }
    public string? Docs { get; set; }
}

/// <summary>Per-folder metadata file (one <c>folder.json</c> per folder when any of these
/// fields are non-empty). Empty folders skip the file altogether — the on-disk layout still
/// represents the folder via its directory.</summary>
public sealed class FolderFile
{
    public string Name { get; set; } = string.Empty;
    public List<KvDto>? Variables { get; set; }
    public List<KvDto>? Headers { get; set; }
    public AuthDto? Auth { get; set; }
    public string? PreRequestScript { get; set; }
    public string? PostResponseScript { get; set; }
    public string? TestsScript { get; set; }
    public string? Docs { get; set; }
}

public sealed class RequestFile
{
    public string Name { get; set; } = string.Empty;
    public RequestKind Kind { get; set; } = RequestKind.Http;
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public List<KvDto>? Params { get; set; }
    public List<KvDto>? PathParams { get; set; }
    public List<KvDto>? Headers { get; set; }
    public BodyDto? Body { get; set; }
    public AuthDto? Auth { get; set; }
    public List<KvDto>? PreRequestVars { get; set; }
    public List<KvDto>? PostResponseVars { get; set; }
    public string? PreRequestScript { get; set; }
    public string? PostResponseScript { get; set; }
    public string? Tests { get; set; }
    public string? Docs { get; set; }
    public RequestSettingsDto? Settings { get; set; }

    public RequestItem ToDomain() => new()
    {
        Name = Name,
        Kind = Kind,
        Method = Method,
        Url = Url,
        Sequence = Sequence,
        Params = (Params ?? new()).Select(KvDto.ToDomain).ToList(),
        PathParams = (PathParams ?? new()).Select(KvDto.ToDomain).ToList(),
        Headers = (Headers ?? new()).Select(KvDto.ToDomain).ToList(),
        Body = Body?.ToDomain() ?? new BodyConfig(),
        Auth = Auth?.ToDomain(),
        PreRequestVars = (PreRequestVars ?? new()).Select(KvDto.ToDomain).ToList(),
        PostResponseVars = (PostResponseVars ?? new()).Select(KvDto.ToDomain).ToList(),
        PreRequestScript = PreRequestScript,
        PostResponseScript = PostResponseScript,
        Tests = Tests,
        Docs = Docs,
        Settings = Settings?.ToDomain() ?? new RequestSettingsConfig(),
    };

    public static RequestFile FromDomain(RequestItem r) => new()
    {
        Name = r.Name,
        Kind = r.Kind,
        Method = r.Method,
        Url = r.Url,
        Sequence = r.Sequence,
        Params = r.Params.Count == 0 ? null : r.Params.Select(KvDto.FromDomain).ToList(),
        PathParams = r.PathParams.Count == 0 ? null : r.PathParams.Select(KvDto.FromDomain).ToList(),
        Headers = r.Headers.Count == 0 ? null : r.Headers.Select(KvDto.FromDomain).ToList(),
        Body = r.Body.Mode == BodyMode.None && string.IsNullOrEmpty(r.Body.Content) ? null : BodyDto.FromDomain(r.Body),
        Auth = r.Auth is null ? null : AuthDto.FromDomain(r.Auth),
        PreRequestVars = r.PreRequestVars.Count == 0 ? null : r.PreRequestVars.Select(KvDto.FromDomain).ToList(),
        PostResponseVars = r.PostResponseVars.Count == 0 ? null : r.PostResponseVars.Select(KvDto.FromDomain).ToList(),
        PreRequestScript = string.IsNullOrEmpty(r.PreRequestScript) ? null : r.PreRequestScript,
        PostResponseScript = string.IsNullOrEmpty(r.PostResponseScript) ? null : r.PostResponseScript,
        Tests = string.IsNullOrEmpty(r.Tests) ? null : r.Tests,
        Docs = string.IsNullOrEmpty(r.Docs) ? null : r.Docs,
        Settings = RequestSettingsDto.FromDomain(r.Settings),
    };
}

public sealed class EnvironmentFile
{
    /// <summary>Stable identity. Persisted at the file root. Old env files predate this field
    /// — <see cref="ToDomain"/> mints a fresh Guid for them so each env always has an id.</summary>
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<KvDto>? Variables { get; set; }
    public List<string>? SecretVariables { get; set; }
    /// <summary>Optional hex color (e.g. <c>"#10B981"</c>). Persisted at the env file root so
    /// the UI can render colored pills/dots without a sidecar.</summary>
    public string? Color { get; set; }

    public static Domain.Environment ToDomain(EnvironmentFile f) => new()
    {
        Id = string.IsNullOrWhiteSpace(f.Id) ? Guid.NewGuid().ToString("N") : f.Id!,
        Name = f.Name,
        Variables = (f.Variables ?? new()).Select(KvDto.ToDomain).ToList(),
        SecretVariables = f.SecretVariables ?? new List<string>(),
        Color = f.Color,
    };

    public static EnvironmentFile FromDomain(Domain.Environment e) => new()
    {
        // Same back-compat path: an Environment built from a .bru file (no id field) gets a
        // fresh Guid on first serialize, locking identity from then on.
        Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id,
        Name = e.Name,
        Variables = e.Variables.Count == 0 ? null : e.Variables.Select(KvDto.FromDomain).ToList(),
        SecretVariables = e.SecretVariables.Count == 0 ? null : e.SecretVariables.ToList(),
        Color = e.Color,
    };
}

public sealed class KvDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }

    public static KvPair ToDomain(KvDto d) => new(d.Name, d.Value, d.Enabled) { Description = d.Description };
    public static KvDto FromDomain(KvPair k) => new()
    {
        Name = k.Name,
        Value = k.Value,
        Enabled = k.Enabled,
        Description = k.Description,
    };
}

public sealed class BodyDto
{
    public BodyMode Mode { get; set; } = BodyMode.None;
    public string? Content { get; set; }
    public List<KvDto>? FormData { get; set; }
    /// <summary>Multipart-form rows. Persisted only when non-empty so the JSON for legacy
    /// requests (or non-multipart modes) stays minimal.</summary>
    public List<MultipartItemDto>? MultipartItems { get; set; }
    public string? FilePath { get; set; }
    public string? FileContentType { get; set; }
    public string? GraphQLQuery { get; set; }
    public string? GraphQLVariables { get; set; }

    public BodyConfig ToDomain() => new()
    {
        Mode = Mode,
        Content = Content,
        FormData = (FormData ?? new()).Select(KvDto.ToDomain).ToList(),
        MultipartItems = (MultipartItems ?? new()).Select(MultipartItemDto.ToDomain).ToList(),
        FilePath = FilePath,
        FileContentType = FileContentType,
        GraphQLQuery = GraphQLQuery,
        GraphQLVariables = GraphQLVariables,
    };

    public static BodyDto FromDomain(BodyConfig b) => new()
    {
        Mode = b.Mode,
        Content = b.Content,
        FormData = b.FormData.Count == 0 ? null : b.FormData.Select(KvDto.FromDomain).ToList(),
        MultipartItems = b.MultipartItems.Count == 0 ? null : b.MultipartItems.Select(MultipartItemDto.FromDomain).ToList(),
        FilePath = string.IsNullOrEmpty(b.FilePath) ? null : b.FilePath,
        FileContentType = string.IsNullOrEmpty(b.FileContentType) ? null : b.FileContentType,
        GraphQLQuery = b.GraphQLQuery,
        GraphQLVariables = b.GraphQLVariables,
    };
}

/// <summary>JSON shape of <see cref="MultipartFormItem"/>. Kind = "text" / "file";
/// other fields are 1-to-1 with the domain record.</summary>
public sealed class MultipartItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Kind { get; set; } = "text";
    public string? ContentType { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }

    public static MultipartFormItem ToDomain(MultipartItemDto d) => new()
    {
        Name = d.Name,
        Value = d.Value,
        Kind = string.IsNullOrEmpty(d.Kind) ? "text" : d.Kind,
        ContentType = d.ContentType,
        Enabled = d.Enabled,
        Description = d.Description,
    };

    public static MultipartItemDto FromDomain(MultipartFormItem i) => new()
    {
        Name = i.Name,
        Value = i.Value,
        Kind = i.Kind,
        ContentType = i.ContentType,
        Enabled = i.Enabled,
        Description = i.Description,
    };
}

public sealed class AuthDto
{
    public AuthType Type { get; set; } = AuthType.None;
    public Dictionary<string, string>? Parameters { get; set; }

    public AuthConfig ToDomain() => new()
    {
        Type = Type,
        Parameters = Parameters ?? new Dictionary<string, string>(),
    };

    public static AuthDto FromDomain(AuthConfig a) => new()
    {
        Type = a.Type,
        Parameters = a.Parameters.Count == 0 ? null : new Dictionary<string, string>(a.Parameters),
    };
}

public sealed class RequestSettingsDto
{
    public bool FollowRedirects { get; set; } = true;
    public bool VerifySsl { get; set; } = true;
    public bool EncodeUrl { get; set; } = true;
    public bool SendCookies { get; set; } = true;
    public bool SaveCookies { get; set; } = true;
    public bool EnableHttp2 { get; set; } = false;

    public RequestSettingsConfig ToDomain() => new()
    {
        FollowRedirects = FollowRedirects,
        VerifySsl = VerifySsl,
        EncodeUrl = EncodeUrl,
        SendCookies = SendCookies,
        SaveCookies = SaveCookies,
        EnableHttp2 = EnableHttp2,
    };

    public static RequestSettingsDto FromDomain(RequestSettingsConfig s) => new()
    {
        FollowRedirects = s.FollowRedirects,
        VerifySsl = s.VerifySsl,
        EncodeUrl = s.EncodeUrl,
        SendCookies = s.SendCookies,
        SaveCookies = s.SaveCookies,
        EnableHttp2 = s.EnableHttp2,
    };
}

[JsonSerializable(typeof(CollectionFile))]
[JsonSerializable(typeof(FolderFile))]
[JsonSerializable(typeof(RequestFile))]
[JsonSerializable(typeof(EnvironmentFile))]
[JsonSerializable(typeof(KvDto))]
[JsonSerializable(typeof(BodyDto))]
[JsonSerializable(typeof(MultipartItemDto))]
[JsonSerializable(typeof(AuthDto))]
[JsonSerializable(typeof(RequestSettingsDto))]
[JsonSerializable(typeof(List<CollectionFile>))]
[JsonSerializable(typeof(List<RequestFile>))]
[JsonSerializable(typeof(List<EnvironmentFile>))]
[JsonSerializable(typeof(List<KvDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class CollectionJsonContext : JsonSerializerContext { }
