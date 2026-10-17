using LibGit2Sharp;

namespace Vegha.Integrations.Git;

/// <summary>Finds the git repository that encloses a collection / workspace folder. A collection
/// is rarely the repo root in practice — users clone a whole workspace (or a monorepo that
/// contains collections in a subfolder) from the command line, so the Source Control panel
/// must walk up the tree the same way <c>git</c> does instead of treating "folder has no
/// <c>.git</c>" as "not a repo". Treating it that way used to offer <c>Initialize</c>, which
/// created a nested repo with no remote and made every push fail.</summary>
public static class GitRepoLocator
{
    /// <summary>Returns the enclosing repo for <paramref name="folder"/>, or null when the folder
    /// (and every parent) is outside any repository. Bare repos are ignored — Vegha needs a
    /// working tree.</summary>
    public static GitRepoContext? Locate(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        string? discovered;
        try { discovered = Repository.Discover(folder); }
        catch { return null; }
        if (string.IsNullOrEmpty(discovered)) return null;

        try
        {
            using var repo = new Repository(discovered);
            if (repo.Info.IsBare || string.IsNullOrEmpty(repo.Info.WorkingDirectory)) return null;
            var root = Normalize(repo.Info.WorkingDirectory);
            var gitDir = Normalize(repo.Info.Path);
            var prefix = RelativePrefix(root, Normalize(folder));
            // A linked worktree / submodule has a ".git" *file* pointing elsewhere.
            var isWorktree = File.Exists(Path.Combine(root, ".git"));
            // libgit2 ignores sparse checkout: files outside the sparse set show up as deleted
            // and "Stage All" would commit their deletion. Refuse to manage such repos.
            if (UsesSparseCheckout(gitDir))
                return new GitRepoContext(root, gitDir, prefix, isWorktree) { UnsupportedReason = SparseMessage };
            return new GitRepoContext(root, gitDir, prefix, isWorktree);
        }
        catch (LibGit2SharpException ex)
        {
            // A real repository that libgit2 can't open (e.g. sparse checkout →
            // extensions.worktreeConfig). Report it instead of pretending there's no repo —
            // otherwise the panel would offer to "publish" (re-init) an existing repository.
            var gitPath = discovered.TrimEnd('\\', '/');
            var root = Path.GetFileName(gitPath).Equals(".git", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(gitPath) ?? gitPath
                : gitPath;
            return new GitRepoContext(Normalize(root), Normalize(gitPath), RelativePrefix(Normalize(root), Normalize(folder)), false)
            {
                UnsupportedReason = ex.Message.Contains("worktreeconfig", StringComparison.OrdinalIgnoreCase)
                    ? SparseMessage
                    : $"Vegha can't open this repository: {ex.Message}",
            };
        }
        catch
        {
            return null;
        }
    }

    private const string SparseMessage =
        "This repository uses sparse checkout (or per-worktree config), which Vegha's Git engine can't manage safely yet — files outside the sparse set would look deleted. Use the git command line for this repository, or clone it without sparse checkout.";

    /// <summary>True when core.sparseCheckout is enabled in the repo config or in
    /// config.worktree (where <c>git sparse-checkout</c> writes it; libgit2 doesn't read that file).</summary>
    public static bool UsesSparseCheckout(string gitDir)
    {
        foreach (var name in new[] { "config", "config.worktree" })
        {
            var path = Path.Combine(gitDir, name);
            try
            {
                if (!File.Exists(path)) continue;
                var inCore = false;
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[')) { inCore = line.StartsWith("[core]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inCore) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (line[..eq].Trim().Equals("sparsecheckout", StringComparison.OrdinalIgnoreCase)
                        && line[(eq + 1)..].Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* unreadable config → assume not sparse */ }
        }
        return false;
    }

    /// <summary>Repo-relative, forward-slash prefix of <paramref name="folder"/> under
    /// <paramref name="root"/> ("" when they are the same folder).</summary>
    public static string RelativePrefix(string root, string folder)
    {
        var rel = Path.GetRelativePath(root, folder);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return string.Empty;
        return rel.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>True when the repo-relative <paramref name="path"/> sits under
    /// <paramref name="prefix"/> (always true for an empty prefix).</summary>
    public static bool IsUnderPrefix(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return true;
        var p = path.Replace('\\', '/');
        return p.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p.TrimEnd('\\', '/'); }
    }
}

/// <summary>Where a collection lives inside its repository.</summary>
/// <param name="RepoRoot">Absolute working-tree root (what git calls the toplevel).</param>
/// <param name="GitDir">Absolute path to the git metadata dir (may be outside RepoRoot for worktrees).</param>
/// <param name="CollectionPrefix">Repo-relative forward-slash path of the collection folder; "" when it is the root.</param>
/// <param name="IsWorktree">True when RepoRoot/.git is a file (linked worktree or submodule).</param>
public sealed record GitRepoContext(string RepoRoot, string GitDir, string CollectionPrefix, bool IsWorktree)
{
    /// <summary>Set when a repository exists but can't be managed by Vegha (unsupported
    /// repository extensions). Callers must not run libgit2 operations on it.</summary>
    public string? UnsupportedReason { get; init; }

    public bool IsSupported => UnsupportedReason is null;
}
