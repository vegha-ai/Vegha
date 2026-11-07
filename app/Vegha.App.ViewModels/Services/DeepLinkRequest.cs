using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;

namespace Vegha.App.ViewModels.Services;

/// <summary>What a <c>vegha://</c> link asks the app to do.</summary>
public enum DeepLinkKind
{
    /// <summary><c>vegha://import/git?url=…[&amp;branch=…][&amp;folder=…]</c> — open the
    /// "Open from Git" dialog with the URL pre-filled. The user still has to click Clone.</summary>
    ImportGit,
}

/// <summary>A parsed <c>vegha://</c> deep link. Links arrive from the OS (a browser handed a
/// <c>vegha:</c> URL to us), so every field is untrusted input: the parser whitelists the
/// route, requires a remote http(s)/ssh git URL, and strips anything that could reach the
/// file system (<c>file:</c> URLs, local paths, <c>..</c> folder segments).</summary>
public sealed record DeepLinkRequest(DeepLinkKind Kind, string Url, string? Branch, string? Folder)
{
    public const string Scheme = "vegha";

    /// <summary>True when a command-line argument looks like one of our links.</summary>
    public static bool IsDeepLink(string? arg)
        => arg is not null && arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a link; <paramref name="error"/> explains a rejection in user-facing terms.</summary>
    public static DeepLinkRequest? TryParse(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw) || !IsDeepLink(raw))
        {
            error = "Not a vegha:// link.";
            return null;
        }
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = "The link is malformed.";
            return null;
        }

        // Browsers emit vegha://import/git?…, where "import" lands in Host and "/git" in
        // AbsolutePath; a hand-typed vegha:///import/git has an empty host. Both map to the
        // same route.
        var route = (uri.Host + uri.AbsolutePath).Trim('/').ToLowerInvariant();
        var query = ParseQuery(uri.Query);

        switch (route)
        {
            case "import/git":
            {
                if (!query.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
                {
                    error = "The link does not say which repository to open (missing url).";
                    return null;
                }
                url = url.Trim();
                var clone = GitProviderRegistry.ParseWebUrl(url)?.CloneUrl ?? url;
                var remote = GitRemoteUrl.TryParse(clone);
                if (remote is null || remote.Scheme is not ("https" or "http" or "ssh") || string.IsNullOrEmpty(remote.Host))
                {
                    error = "Only remote git repositories (https:// or ssh) can be opened from a link.";
                    return null;
                }
                query.TryGetValue("branch", out var branch);
                query.TryGetValue("folder", out var folder);
                return new DeepLinkRequest(DeepLinkKind.ImportGit, url, CleanBranch(branch), CleanFolder(folder));
            }
            default:
                error = $"Unknown link action \"{route}\".";
                return null;
        }
    }

    private static string? CleanBranch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        // git ref names never contain whitespace or control characters.
        return value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : value;
    }

    private static string? CleanFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts.Any(p => p is "." or ".." || p.Any(char.IsControl) || p.Contains(':'))) return null;
        return string.Join('/', parts);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).Trim();
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key.Length > 0 && !result.ContainsKey(key)) result[key] = value;
        }
        return result;
    }
}
