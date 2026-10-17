namespace Vegha.Integrations.Git.Credentials;

/// <summary>Parsed remote URL. Handles <c>https://</c>, <c>ssh://</c>, scp-style
/// <c>git@host:owner/repo.git</c>, and Azure DevOps' legacy <c>{org}.visualstudio.com</c> and
/// SSH <c>ssh.dev.azure.com:v3/org/project/repo</c> forms.</summary>
public sealed record GitRemoteUrl(string Scheme, string Host, int? Port, string Path, string? User)
{
    public bool IsSsh => Scheme is "ssh";
    public bool IsHttp => Scheme is "https" or "http";

    /// <summary>Path without a trailing <c>.git</c>.</summary>
    public string PathWithoutGit => Path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? Path[..^4] : Path;

    /// <summary>Last path segment without <c>.git</c> — the natural folder name for a clone.</summary>
    public string RepoName
    {
        get
        {
            // Local paths on Windows use backslashes.
            var segs = PathWithoutGit.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segs.Length == 0 ? Host : segs[^1];
        }
    }

    /// <summary>HTTPS origin for credential-helper / header scoping, e.g. <c>https://dev.azure.com</c>.</summary>
    public string HttpsOrigin => $"{(Scheme == "http" ? "http" : "https")}://{Host}{(Port is { } p && IsHttp ? ":" + p : "")}";

    public override string ToString() => IsSsh
        ? $"ssh://{(User is null ? "" : User + "@")}{Host}{(Port is { } p ? ":" + p : "")}/{Path}"
        : $"{Scheme}://{Host}{(Port is { } q ? ":" + q : "")}/{Path}";

    public static GitRemoteUrl? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is "git+ssh" or "ssh+git") scheme = "ssh";
            if (scheme is "file") return new GitRemoteUrl("file", "", null, uri.LocalPath, null);
            var user = string.IsNullOrEmpty(uri.UserInfo) ? null : Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            return new GitRemoteUrl(scheme, uri.Host.ToLowerInvariant(), uri.IsDefaultPort ? null : uri.Port, path, user);
        }

        // scp-like: [user@]host:path  (but not a Windows drive path like C:\repo)
        var colon = url.IndexOf(':');
        if (colon > 1 && !url.StartsWith('/') && !(colon == 1 && char.IsLetter(url[0])))
        {
            var left = url[..colon];
            var right = url[(colon + 1)..];
            string? user = null;
            var at = left.LastIndexOf('@');
            if (at >= 0) { user = left[..at]; left = left[(at + 1)..]; }
            if (left.Length == 0 || left.Contains('/') || left.Contains('\\')) return null;
            return new GitRemoteUrl("ssh", left.ToLowerInvariant(), null, right.TrimStart('/'), user);
        }

        // Local path.
        if (Directory.Exists(url) || url.StartsWith('/') || (url.Length > 2 && url[1] == ':'))
            return new GitRemoteUrl("file", "", null, url, null);
        return null;
    }
}
