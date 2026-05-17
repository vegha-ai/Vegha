using LibGit2Sharp;

namespace Vegha.Integrations.Git;

/// <summary>
/// One-shot Git clone helper used by the unified Import dialog (Git + GitHub tabs).
/// Distinct from <see cref="GitService"/> which manages an existing repo's status /
/// commits / push-pull — clone is a different ceremony and lives here so the
/// Import wizard doesn't have to reach into general-purpose Git plumbing.
/// </summary>
public static class GitCloneService
{
    /// <summary>Clones <paramref name="url"/> into <paramref name="destination"/>. If
    /// <paramref name="branch"/> is supplied, only that branch is fetched and the
    /// working tree checks out to it. Throws on failure.</summary>
    public static void Clone(string url, string destination, string? branch, GitCloneCredentials? credentials)
    {
        var options = new CloneOptions();
        if (!string.IsNullOrEmpty(branch)) options.BranchName = branch;
        if (credentials is not null)
        {
            options.FetchOptions.CredentialsProvider = (_, _, _) => BuildCredentials(credentials);
        }
        Repository.Clone(url, destination, options);
    }

    private static Credentials BuildCredentials(GitCloneCredentials c) => c.Mode switch
    {
        "https-pat" => new UsernamePasswordCredentials
        {
            Username = c.Username ?? "x-access-token",
            Password = c.Password ?? string.Empty,
        },
        // SSH key auth requires native libssh2 / specific platform builds of LibGit2Sharp;
        // the bundled package surfaces SSH only via the URL (git@…) path with a
        // pre-configured agent. Fall through to default credentials for ssh-key mode and
        // let LibGit2Sharp's native machinery pick up a configured agent if available.
        "ssh-key" => new DefaultCredentials(),
        _ => new DefaultCredentials(),
    };
}

/// <summary>Credentials shape for <see cref="GitCloneService.Clone"/>. Use the static
/// factory methods rather than constructing directly so the <c>Mode</c> string stays
/// in sync with the dispatch in <see cref="GitCloneService"/>.</summary>
public sealed record GitCloneCredentials(string Mode, string? Username, string? Password, string? SshKeyPath)
{
    public static GitCloneCredentials HttpsPat(string username, string pat) =>
        new("https-pat", username, pat, null);

    public static GitCloneCredentials SshKey(string privateKeyPath) =>
        new("ssh-key", null, null, privateKeyPath);
}
