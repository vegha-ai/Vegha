using System.Text.RegularExpressions;

namespace Vegha.Integrations.Git;

/// <summary>What went wrong in a git operation, in terms the UI can act on.</summary>
public enum GitErrorKind
{
    Unknown,
    GitNotInstalled,
    NoRemoteConfigured,
    RemoteNotFound,
    AuthFailed,
    SamlSsoAuthorizationRequired,
    RepoNotFoundOrNoAccess,
    HostKeyUnknown,
    HostKeyChanged,
    PermissionDeniedPublicKey,
    SslCertProblem,
    ProxyAuthRequired,
    ProxyConnectFailed,
    HostUnreachable,
    NonFastForward,
    UnrelatedHistories,
    MergeConflict,
    LocalChangesWouldBeOverwritten,
    LfsMissing,
    NoUpstream,
    Timeout,
    Cancelled,
}

/// <summary>Remedy buttons the error banner can offer.</summary>
public enum GitErrorAction
{
    AddRemote,
    PublishRepository,
    SignIn,
    ConfigureCredentials,
    TrustHost,
    OpenSsoAuthorization,
    PullFirst,
    OpenDiagnostics,
    OpenNetworkSettings,
    InstallGit,
    ResolveConflicts,
    StashAndRetry,
}

/// <summary>A classified error: kind + human-readable title/explanation + suggested actions.</summary>
public sealed record GitErrorInfo(
    GitErrorKind Kind,
    string Title,
    string Explanation,
    IReadOnlyList<GitErrorAction> Actions,
    string? Url = null,
    string? Detail = null);

/// <summary>Maps git / GCM / ssh stderr into <see cref="GitErrorInfo"/>. git runs with
/// <c>LC_ALL=C</c> (see <see cref="GitEnvironment"/>) so the messages are stable English.
/// Patterns cover GitHub (incl. SAML SSO), Azure DevOps (TF400813 / TF401019), generic HTTP,
/// SSH and proxy failures.</summary>
public static partial class GitErrorClassifier
{
    public static GitErrorInfo Classify(string? stderr, string operation = "")
    {
        var s = stderr ?? string.Empty;

        if (Has(s, "does not appear to be a git repository") && RemoteNameRx().Match(s) is { Success: true } rn
            && !rn.Groups[1].Value.Contains('/') && !rn.Groups[1].Value.Contains(':'))
            return new(GitErrorKind.RemoteNotFound, $"Remote '{rn.Groups[1].Value}' doesn't exist",
                $"This repository has no remote named '{rn.Groups[1].Value}'. Add a remote (or pick the right one) before you {VerbFor(operation)}.",
                new[] { GitErrorAction.AddRemote, GitErrorAction.PublishRepository }, Detail: rn.Groups[1].Value);

        if (Has(s, "No configured push destination") || Has(s, "No remote repository specified"))
            return NoRemote(operation);

        if (Has(s, "has no upstream branch") || Has(s, "There is no tracking information for the current branch"))
            return new(GitErrorKind.NoUpstream, "Branch isn't tracking a remote branch",
                "Push the branch first to set its upstream, then pull.", new[] { GitErrorAction.AddRemote });

        var sso = SamlRx().Match(s);
        if (sso.Success || (Has(s, "SAML") && Has(s, "SSO")))
            return new(GitErrorKind.SamlSsoAuthorizationRequired, "Authorize your token for SSO",
                "Your organization enforces SAML single sign-on. The credential works, but it has not been authorized for this organization yet. Open the authorization page, approve, then retry.",
                new[] { GitErrorAction.OpenSsoAuthorization, GitErrorAction.SignIn }, Url: sso.Success ? sso.Groups[1].Value : null);

        if (Has(s, "REMOTE HOST IDENTIFICATION HAS CHANGED"))
            return new(GitErrorKind.HostKeyChanged, "SSH host key changed",
                "The server's SSH key doesn't match the one in known_hosts. This can mean the host was re-keyed — or a man-in-the-middle. Verify the fingerprint with your administrator before removing the old entry.",
                new[] { GitErrorAction.OpenDiagnostics });

        if (Has(s, "Host key verification failed") || Has(s, "No ED25519 host key is known") || Has(s, "authenticity of host"))
            return new(GitErrorKind.HostKeyUnknown, "Unknown SSH host",
                "This is the first SSH connection to this host from Vegha. Verify and trust the host key, then retry.",
                new[] { GitErrorAction.TrustHost, GitErrorAction.OpenDiagnostics }, Detail: HostFingerprintRx().Match(s) is { Success: true } fp ? fp.Groups[1].Value : null);

        if (Has(s, "Permission denied (publickey") || Has(s, "Permission denied, please try again"))
            return new(GitErrorKind.PermissionDeniedPublicKey, "SSH key rejected",
                "The server rejected your SSH key. Make sure the public key is added to your account, or choose a different key for this host.",
                new[] { GitErrorAction.ConfigureCredentials, GitErrorAction.OpenDiagnostics });

        if (Has(s, "407 Proxy Authentication Required") || Has(s, "Proxy CONNECT aborted") && Has(s, "407"))
            return new(GitErrorKind.ProxyAuthRequired, "Proxy needs credentials",
                "Your corporate proxy requires authentication. Set the proxy username and password in Settings → Network.",
                new[] { GitErrorAction.OpenNetworkSettings, GitErrorAction.OpenDiagnostics });

        if (Has(s, "Failed to connect to") && Has(s, "proxy") || Has(s, "Could not resolve proxy") || Has(s, "Proxy CONNECT aborted"))
            return new(GitErrorKind.ProxyConnectFailed, "Couldn't reach the proxy",
                "Git couldn't connect through the configured proxy. Check Settings → Network.",
                new[] { GitErrorAction.OpenNetworkSettings, GitErrorAction.OpenDiagnostics });

        if (Has(s, "SSL certificate problem") || Has(s, "server certificate verification failed") || Has(s, "schannel:") && Has(s, "certificate")
            || Has(s, "unable to get local issuer certificate") || Has(s, "self signed certificate") || Has(s, "self-signed certificate"))
            return new(GitErrorKind.SslCertProblem, "TLS certificate not trusted",
                "The server's certificate isn't trusted. Behind TLS-inspecting proxies, use the operating system certificate store (Settings → Git → Use OS certificate store) or ask IT for the root CA.",
                new[] { GitErrorAction.OpenDiagnostics, GitErrorAction.OpenNetworkSettings });

        if (Has(s, "TF400813") || Has(s, "Authentication failed") || Has(s, "could not read Username")
            || Has(s, "could not read Password") || Has(s, "Invalid username or password") || Has(s, "HTTP Basic: Access denied")
            || Has(s, "The requested URL returned error: 401") || Has(s, "terminal prompts disabled")
            || Has(s, "Password authentication is not supported") || Has(s, "Invalid username or token"))
            return new(GitErrorKind.AuthFailed, "Sign-in required",
                "Git couldn't authenticate to the remote. Sign in with your browser (supports SSO + MFA), or configure a personal access token or SSH key for this host.",
                new[] { GitErrorAction.SignIn, GitErrorAction.ConfigureCredentials, GitErrorAction.OpenDiagnostics });

        if (Has(s, "TF401019") || Has(s, "Repository not found") || Has(s, "The requested URL returned error: 403")
            || Has(s, "The requested URL returned error: 404") || Has(s, "does not exist or you do not have permissions")
            || Has(s, "ERROR: Repository not found") || Has(s, "Write access to repository not granted") || Has(s, "Permission to") && Has(s, "denied"))
            return new(GitErrorKind.RepoNotFoundOrNoAccess, "Repository not found or no access",
                "The repository doesn't exist, or the signed-in account can't access it. Check the URL and which account/token is used for this host.",
                new[] { GitErrorAction.ConfigureCredentials, GitErrorAction.OpenDiagnostics });

        if (Has(s, "does not appear to be a git repository") || Has(s, "Could not read from remote repository"))
            return new(GitErrorKind.RepoNotFoundOrNoAccess, "Couldn't read from the remote",
                "The remote URL is wrong, or you don't have access to it.",
                new[] { GitErrorAction.ConfigureCredentials, GitErrorAction.OpenDiagnostics });

        if (Has(s, "Could not resolve host") || Has(s, "Failed to connect to") || Has(s, "Connection timed out")
            || Has(s, "Connection refused") || Has(s, "Network is unreachable") || Has(s, "Couldn't connect to server"))
            return new(GitErrorKind.HostUnreachable, "Can't reach the server",
                "Check your network/VPN connection and proxy settings.",
                new[] { GitErrorAction.OpenNetworkSettings, GitErrorAction.OpenDiagnostics });

        if (Has(s, "refusing to merge unrelated histories"))
            return new(GitErrorKind.UnrelatedHistories, "Remote has unrelated history",
                "The remote already has commits that don't share history with your local repository.",
                new[] { GitErrorAction.OpenDiagnostics });

        if (Has(s, "[rejected]") && (Has(s, "non-fast-forward") || Has(s, "fetch first")) || Has(s, "Updates were rejected"))
            return new(GitErrorKind.NonFastForward, "Remote has newer commits",
                "Someone pushed changes you don't have yet. Pull first, then push again.",
                new[] { GitErrorAction.PullFirst });

        if (Has(s, "Not possible to fast-forward") || Has(s, "Diverging branches can't be fast-forwarded"))
            return new(GitErrorKind.NonFastForward, "Branches have diverged",
                "Your branch and the remote branch both have new commits. Change Settings → Git → Pull behavior to Rebase or Merge, or pull from the terminal.",
                new[] { GitErrorAction.OpenDiagnostics });

        if (Has(s, "CONFLICT (") || Has(s, "Resolve all conflicts manually") || Has(s, "could not apply"))
            return new(GitErrorKind.MergeConflict, "Conflicts need resolving",
                "Some files changed on both sides. Resolve the conflicts in the Merge Changes section, then continue.",
                new[] { GitErrorAction.ResolveConflicts });

        if (Has(s, "would be overwritten by") || Has(s, "Please commit your changes or stash them"))
            return new(GitErrorKind.LocalChangesWouldBeOverwritten, "Local changes in the way",
                "Commit or stash your local changes first.",
                new[] { GitErrorAction.StashAndRetry });

        if (Has(s, "git-lfs") && (Has(s, "not found") || Has(s, "command not found") || Has(s, "is not a git command")))
            return new(GitErrorKind.LfsMissing, "Git LFS is not installed",
                "This repository uses Git LFS. Install Git LFS and retry.", Array.Empty<GitErrorAction>());

        return new(GitErrorKind.Unknown, $"Git {(string.IsNullOrEmpty(operation) ? "operation" : operation)} failed",
            FirstMeaningfulLine(s) ?? "Git reported an error. See the Git output for details.",
            new[] { GitErrorAction.OpenDiagnostics });
    }

    public static GitErrorInfo NoRemote(string operation = "") =>
        new(GitErrorKind.NoRemoteConfigured, "No remote configured",
            $"This repository isn't connected to a remote yet. Add a remote URL or publish it to GitHub / Azure DevOps to {VerbFor(operation)}.",
            new[] { GitErrorAction.PublishRepository, GitErrorAction.AddRemote });

    public static GitErrorInfo NotInstalled() =>
        new(GitErrorKind.GitNotInstalled, "Git isn't installed",
            "Vegha uses Git for sync. Install it, or let Vegha download a private copy.",
            new[] { GitErrorAction.InstallGit });

    private static string VerbFor(string op) => op switch
    {
        "push" => "push",
        "pull" => "pull",
        "fetch" => "fetch",
        "sync" => "sync",
        _ => "sync",
    };

    private static bool Has(string s, string needle) => s.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? FirstMeaningfulLine(string s) =>
        s.Split('\n').Select(l => l.Trim())
         .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        ?? s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    [GeneratedRegex(@"'([^']+)' does not appear to be a git repository")]
    private static partial Regex RemoteNameRx();

    [GeneratedRegex(@"(https://[^\s]+/sso\?authorization_request=[^\s]+)")]
    private static partial Regex SamlRx();

    [GeneratedRegex(@"(SHA256:[A-Za-z0-9+/=]+)")]
    private static partial Regex HostFingerprintRx();
}
