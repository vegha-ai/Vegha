using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vegha.Integrations.Git.Credentials;

namespace Vegha.Integrations.Git.Providers;

/// <summary>Hosting-provider adapter: URL normalization, credential hints, deep links, and the
/// REST calls behind the repo picker / "create remote repository" in the Publish wizard.</summary>
public interface IGitProvider
{
    string Name { get; }
    bool Matches(string host);

    /// <summary>Canonical clone URL (e.g. legacy <c>org.visualstudio.com</c> → <c>dev.azure.com/org</c>,
    /// strips embedded <c>user@</c> from HTTPS URLs so the credential helper can choose the account).</summary>
    string Normalize(string url);

    /// <summary>Page where the user creates a personal access token.</summary>
    string? PatCreateUrl(GitRemoteUrl remote);

    /// <summary>Short hint shown next to the PAT field ("Scopes: repo", "Code: Read & write").</summary>
    string PatHint { get; }

    Task<IReadOnlyList<GitProviderOwner>> ListOwnersAsync(GitProviderAccount account, CancellationToken ct);
    Task<IReadOnlyList<GitProviderRepo>> ListReposAsync(GitProviderAccount account, GitProviderOwner owner, CancellationToken ct);
    Task<GitProviderRepo> CreateRepoAsync(GitProviderAccount account, GitProviderOwner owner, string name, bool isPrivate, CancellationToken ct);
}

/// <summary>Host + API token for provider calls. <see cref="BaseUrl"/> is the web origin
/// (<c>https://github.com</c>, <c>https://ghe.acme.com</c>, <c>https://dev.azure.com/contoso</c>).</summary>
public sealed record GitProviderAccount(string BaseUrl, string AuthScheme, string Token);

/// <summary>A user / org (GitHub) or project (Azure DevOps) that owns repositories.</summary>
public sealed record GitProviderOwner(string Id, string Name, GitProviderOwnerKind Kind);

public enum GitProviderOwnerKind { User, Organization, Project }

/// <summary>Result of <see cref="GitProviderRegistry.ParseWebUrl"/>.</summary>
public sealed record GitWebUrl(string CloneUrl, string? Branch, string? Folder);

public sealed record GitProviderRepo(string Name, string FullName, string CloneUrl, string? SshUrl, bool IsPrivate, string? DefaultBranch, long? SizeKb);

public static class GitProviderRegistry
{
    private static readonly IGitProvider[] All = { new GitHubProvider(), new AzureDevOpsProvider() };

    /// <summary>Hosts the user registered as GitHub Enterprise Server (Settings → Git).</summary>
    public static IReadOnlyCollection<string> EnterpriseGitHubHosts { get; set; } = Array.Empty<string>();

    public static IGitProvider? For(string? url)
    {
        var r = GitRemoteUrl.TryParse(url);
        return r is null ? null : ForHost(r.Host);
    }

    public static IGitProvider? ForHost(string host)
    {
        if (EnterpriseGitHubHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return All[0];
        return All.FirstOrDefault(p => p.Matches(host));
    }

    public static string Normalize(string url) => For(url)?.Normalize(url) ?? url.Trim();

    /// <summary>Recognizes a URL copied from the browser rather than a clone URL:
    /// GitHub <c>https://host/owner/repo/tree/&lt;branch&gt;/&lt;folder&gt;</c> (or <c>/blob/…/file</c>)
    /// and Azure DevOps <c>…/_git/repo?path=/folder&amp;version=GBbranch</c>. Returns the clone
    /// URL plus the branch and folder to check out, or null when it's not a browser URL.
    /// Branch names containing '/' are ambiguous in GitHub tree URLs; the first segment is used.</summary>
    public static GitWebUrl? ParseWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http")) return null;
        var host = uri.Host.ToLowerInvariant();
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        if (ForHost(host) is GitHubProvider)
        {
            if (segs.Length < 4 || segs[2] is not ("tree" or "blob")) return null;
            var clone = $"{uri.Scheme}://{uri.Authority}/{segs[0]}/{segs[1].Replace(".git", "", StringComparison.OrdinalIgnoreCase)}.git";
            var pathSegs = segs.Skip(4).ToList();
            if (segs[2] == "blob" && pathSegs.Count > 0) pathSegs.RemoveAt(pathSegs.Count - 1); // file → its folder
            return new GitWebUrl(clone, segs[3], pathSegs.Count == 0 ? null : string.Join('/', pathSegs));
        }

        if (GitHostKinds.IsAzureDevOps(host) && !string.IsNullOrEmpty(uri.Query) && segs.Contains("_git"))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.OrdinalIgnoreCase);
            query.TryGetValue("path", out var path);
            query.TryGetValue("version", out var version);
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(version)) return null;
            var branch = version is not null && version.StartsWith("GB", StringComparison.Ordinal) ? version[2..] : null;
            var folder = string.IsNullOrEmpty(path) ? null : path.Trim('/');
            // A path pointing at a file: keep its folder.
            if (folder is not null && Path.HasExtension(folder)) folder = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var clone = new AzureDevOpsProvider().Normalize($"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");
            return new GitWebUrl(clone, branch, string.IsNullOrEmpty(folder) ? null : folder);
        }
        return null;
    }

    internal static HttpClient CreateClient(GitProviderAccount account, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vegha");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(account.AuthScheme, account.Token);
        return http;
    }

    /// <summary>Optional handler factory (proxy-aware) supplied by the app.</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    internal static HttpClient Client(GitProviderAccount account) => CreateClient(account, HandlerFactory?.Invoke());

    internal static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        await EnsureAsync(resp, ct).ConfigureAwait(false);
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    internal static async Task<JsonDocument> PostJsonAsync(HttpClient http, string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        await EnsureAsync(resp, ct).ConfigureAwait(false);
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var sso = resp.Headers.TryGetValues("X-GitHub-SSO", out var v) ? v.FirstOrDefault() : null;
        if (sso is not null)
        {
            var url = sso.Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("url=", StringComparison.Ordinal))?[4..];
            throw new GitProviderException("Your token must be authorized for this organization's SAML SSO.", (int)resp.StatusCode, url);
        }
        var snippet = body.Length > 300 ? body[..300] : body;
        throw new GitProviderException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {snippet}", (int)resp.StatusCode, null);
    }
}

public sealed class GitProviderException : Exception
{
    public int StatusCode { get; }
    public string? SsoAuthorizationUrl { get; }

    public GitProviderException(string message, int statusCode, string? ssoUrl) : base(message)
    {
        StatusCode = statusCode;
        SsoAuthorizationUrl = ssoUrl;
    }
}

/// <summary>GitHub.com, GitHub Enterprise Cloud with data residency (<c>*.ghe.com</c>), and GitHub
/// Enterprise Server (hosts registered in settings; API under <c>/api/v3</c>).</summary>
public sealed class GitHubProvider : IGitProvider
{
    public string Name => "GitHub";

    public bool Matches(string host) => GitHostKinds.IsGitHub(host);

    public string PatHint => "Classic token scope: repo (or a fine-grained token with Contents: Read and write). If your org uses SAML SSO, authorize the token for it.";

    public string Normalize(string url)
    {
        var r = GitRemoteUrl.TryParse(url);
        if (r is null || !r.IsHttp) return url.Trim();
        var path = r.PathWithoutGit.TrimEnd('/');
        return $"https://{r.Host}/{path}.git";
    }

    public string? PatCreateUrl(GitRemoteUrl remote) =>
        $"https://{(remote.Host == "ssh.github.com" ? "github.com" : remote.Host)}/settings/tokens/new?scopes=repo&description=Vegha";

    public static string ApiBase(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return "https://api.github.com";
        if (uri.Host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)) return $"https://api.{uri.Host}";
        return $"{uri.Scheme}://{uri.Authority}/api/v3";
    }

    public async Task<IReadOnlyList<GitProviderOwner>> ListOwnersAsync(GitProviderAccount account, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        var api = ApiBase(account.BaseUrl);
        var owners = new List<GitProviderOwner>();
        using (var me = await GitProviderRegistry.GetJsonAsync(http, $"{api}/user", ct).ConfigureAwait(false))
        {
            var login = me.RootElement.GetProperty("login").GetString()!;
            owners.Add(new GitProviderOwner(login, login, GitProviderOwnerKind.User));
        }
        using (var orgs = await GitProviderRegistry.GetJsonAsync(http, $"{api}/user/orgs?per_page=100", ct).ConfigureAwait(false))
        {
            foreach (var o in orgs.RootElement.EnumerateArray())
            {
                var login = o.GetProperty("login").GetString()!;
                owners.Add(new GitProviderOwner(login, login, GitProviderOwnerKind.Organization));
            }
        }
        return owners;
    }

    public async Task<IReadOnlyList<GitProviderRepo>> ListReposAsync(GitProviderAccount account, GitProviderOwner owner, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        var api = ApiBase(account.BaseUrl);
        var list = new List<GitProviderRepo>();
        for (var page = 1; page <= 10; page++)
        {
            var url = owner.Kind == GitProviderOwnerKind.User
                ? $"{api}/user/repos?per_page=100&page={page}&affiliation=owner,collaborator&sort=updated"
                : $"{api}/orgs/{Uri.EscapeDataString(owner.Name)}/repos?per_page=100&page={page}&sort=updated";
            using var doc = await GitProviderRegistry.GetJsonAsync(http, url, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                count++;
                if (owner.Kind == GitProviderOwnerKind.User &&
                    !string.Equals(r.GetProperty("owner").GetProperty("login").GetString(), owner.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(Map(r));
            }
            if (count < 100) break;
        }
        return list;
    }

    public async Task<GitProviderRepo> CreateRepoAsync(GitProviderAccount account, GitProviderOwner owner, string name, bool isPrivate, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        var api = ApiBase(account.BaseUrl);
        var url = owner.Kind == GitProviderOwnerKind.User ? $"{api}/user/repos" : $"{api}/orgs/{Uri.EscapeDataString(owner.Name)}/repos";
        using var doc = await GitProviderRegistry.PostJsonAsync(http, url, new { name, @private = isPrivate, auto_init = false }, ct).ConfigureAwait(false);
        return Map(doc.RootElement);
    }

    private static GitProviderRepo Map(JsonElement r) => new(
        r.GetProperty("name").GetString()!,
        r.GetProperty("full_name").GetString()!,
        r.GetProperty("clone_url").GetString()!,
        r.TryGetProperty("ssh_url", out var ssh) ? ssh.GetString() : null,
        r.TryGetProperty("private", out var p) && p.GetBoolean(),
        r.TryGetProperty("default_branch", out var b) ? b.GetString() : null,
        r.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null);
}

/// <summary>Azure DevOps Services (<c>dev.azure.com/{org}</c>, legacy <c>{org}.visualstudio.com</c>)
/// and Azure DevOps Server (any host; base URL includes the collection, e.g.
/// <c>https://tfs.acme.com/DefaultCollection</c>).</summary>
public sealed class AzureDevOpsProvider : IGitProvider
{
    private const string ApiVersion = "api-version=7.1";

    public string Name => "Azure DevOps";

    public bool Matches(string host) => GitHostKinds.IsAzureDevOps(host);

    public string PatHint => "Scope: Code (Read & write). Pick the organization (or All accessible organizations).";

    public string Normalize(string url)
    {
        var r = GitRemoteUrl.TryParse(url);
        if (r is null) return url.Trim();
        if (r.IsHttp && r.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && r.Host != "vs-ssh.visualstudio.com")
        {
            var org = r.Host[..r.Host.IndexOf('.')];
            var path = r.Path.StartsWith("DefaultCollection/", StringComparison.OrdinalIgnoreCase) ? r.Path["DefaultCollection/".Length..] : r.Path;
            return $"https://dev.azure.com/{org}/{path}";
        }
        // Strip "org@" that the ADO "Clone" button embeds — it pins the credential account.
        return r.IsHttp ? $"https://{r.Host}{(r.Port is { } p ? ":" + p : "")}/{r.Path}" : url.Trim();
    }

    public string? PatCreateUrl(GitRemoteUrl remote)
    {
        var org = Organization(remote);
        return org is null ? "https://dev.azure.com/_usersSettings/tokens" : $"https://dev.azure.com/{org}/_usersSettings/tokens";
    }

    /// <summary>Organization from an ADO URL (https or ssh v3), or null.</summary>
    public static string? Organization(GitRemoteUrl remote)
    {
        var segs = remote.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (remote.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)) return segs.Length > 0 ? segs[0] : null;
        if (remote.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)) return segs.Length > 1 && segs[0] == "v3" ? segs[1] : null;
        if (remote.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && remote.Host != "vs-ssh.visualstudio.com")
            return remote.Host[..remote.Host.IndexOf('.')];
        return null;
    }

    public async Task<IReadOnlyList<GitProviderOwner>> ListOwnersAsync(GitProviderAccount account, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        using var doc = await GitProviderRegistry.GetJsonAsync(http, $"{account.BaseUrl.TrimEnd('/')}/_apis/projects?$top=500&{ApiVersion}", ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(p => new GitProviderOwner(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!, GitProviderOwnerKind.Project))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GitProviderRepo>> ListReposAsync(GitProviderAccount account, GitProviderOwner owner, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        using var doc = await GitProviderRegistry.GetJsonAsync(http,
            $"{account.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(owner.Name)}/_apis/git/repositories?{ApiVersion}", ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(r => Map(r, owner.Name))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<GitProviderRepo> CreateRepoAsync(GitProviderAccount account, GitProviderOwner owner, string name, bool isPrivate, CancellationToken ct)
    {
        using var http = GitProviderRegistry.Client(account);
        using var doc = await GitProviderRegistry.PostJsonAsync(http,
            $"{account.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(owner.Name)}/_apis/git/repositories?{ApiVersion}",
            new { name, project = new { id = owner.Id } }, ct).ConfigureAwait(false);
        return Map(doc.RootElement, owner.Name);
    }

    private static GitProviderRepo Map(JsonElement r, string project) => new(
        r.GetProperty("name").GetString()!,
        $"{project}/{r.GetProperty("name").GetString()}",
        new AzureDevOpsProvider().Normalize(r.GetProperty("remoteUrl").GetString()!),
        r.TryGetProperty("sshUrl", out var ssh) ? ssh.GetString() : null,
        true,
        r.TryGetProperty("defaultBranch", out var b) && b.GetString() is { } db ? db.Replace("refs/heads/", "", StringComparison.Ordinal) : null,
        r.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() / 1024 : null);
}
