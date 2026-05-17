using LibGit2Sharp;

namespace Vegha.Integrations.Git;

/// <summary>
/// Facade over LibGit2Sharp providing the operations the Vegha UI surfaces in its
/// Source Control panel: status, stage / unstage, commit, branch list / checkout / create
/// / rename / delete, discard, stash, reset, conflict resolution, file blob lookups for
/// the side-by-side diff tab, and remote-tracking metadata.
///
/// Network operations (fetch / pull / push / clone) intentionally live in
/// <see cref="GitProcessRunner"/> + a separate set of async methods — the shell-out path
/// picks up GCM, SSH config, HTTP proxies, and signed-push support, which libgit2sharp's
/// HTTP/SSH transports do not reliably provide on Windows.
///
/// All methods are synchronous and reentrant — every call opens a fresh
/// <see cref="Repository"/>, performs its work, and disposes. The view-model is
/// responsible for keeping these off the UI thread via <c>Task.Run</c>.
/// </summary>
public sealed partial class GitService
{
    private readonly GitProcessRunner _runner;
    private readonly GitCredentialsService? _credentials;

    public GitService() : this(new GitProcessRunner(), null) { }

    public GitService(GitProcessRunner runner, GitCredentialsService? credentials = null)
    {
        _runner = runner;
        _credentials = credentials;
    }

    private static readonly CompareOptions RenameAware = new()
    {
        Similarity = SimilarityOptions.Default,
        IncludeUnmodified = false,
    };

    // ------------------------------------------------------------ Repo basics

    public bool IsRepository(string path) =>
        Repository.IsValid(path);

    public void Init(string path) =>
        Repository.Init(path);

    public IReadOnlyList<GitFileChange> Status(string path)
    {
        using var repo = new Repository(path);
        var entries = new List<GitFileChange>();
        foreach (var item in repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true }))
        {
            // Skip rows that have no tracked status (Ignored or Nonexistent).
            if (item.State == FileStatus.Ignored || item.State == FileStatus.Nonexistent) continue;
            entries.Add(new GitFileChange(item.FilePath, MapStatus(item.State), IsStaged(item.State)));
        }
        return entries;
    }

    // ------------------------------------------------------------ Branches

    public string CurrentBranch(string path)
    {
        using var repo = new Repository(path);
        return repo.Head.FriendlyName;
    }

    public IReadOnlyList<string> Branches(string path)
    {
        using var repo = new Repository(path);
        return repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName).ToList();
    }

    public IReadOnlyList<string> RemoteBranches(string path)
    {
        using var repo = new Repository(path);
        return repo.Branches.Where(b => b.IsRemote).Select(b => b.FriendlyName).ToList();
    }

    /// <summary>Returns all configured remotes (name + URL).</summary>
    public IReadOnlyList<GitRemoteEntry> Remotes(string path)
    {
        using var repo = new Repository(path);
        return repo.Network.Remotes.Select(r => new GitRemoteEntry(r.Name, r.Url, r.PushUrl)).ToList();
    }

    public void AddRemote(string path, string name, string url)
    {
        using var repo = new Repository(path);
        repo.Network.Remotes.Add(name, url);
    }

    public void RemoveRemote(string path, string name)
    {
        using var repo = new Repository(path);
        repo.Network.Remotes.Remove(name);
    }

    /// <summary>Renames a remote. Use <see cref="UpdateRemoteUrl"/> to change the URL of an
    /// existing remote — libgit2sharp's API splits these operations.</summary>
    public void RenameRemote(string path, string oldName, string newName)
    {
        using var repo = new Repository(path);
        repo.Network.Remotes.Rename(oldName, newName);
    }

    public void UpdateRemoteUrl(string path, string name, string url)
    {
        using var repo = new Repository(path);
        repo.Network.Remotes.Update(name, r => r.Url = url);
    }

    public void Checkout(string path, string branchName)
    {
        using var repo = new Repository(path);
        var branch = repo.Branches[branchName] ?? throw new ArgumentException($"Branch '{branchName}' not found");
        Commands.Checkout(repo, branch);
    }

    public void CreateBranch(string path, string branchName)
    {
        using var repo = new Repository(path);
        repo.CreateBranch(branchName);
    }

    public void CreateBranchFrom(string path, string branchName, string baseRef)
    {
        using var repo = new Repository(path);
        var baseCommit = repo.Lookup<Commit>(baseRef)
            ?? repo.Branches[baseRef]?.Tip
            ?? throw new ArgumentException($"Base ref '{baseRef}' not found");
        repo.CreateBranch(branchName, baseCommit);
    }

    public void DeleteBranch(string path, string branchName)
    {
        using var repo = new Repository(path);
        if (string.Equals(repo.Head.FriendlyName, branchName, StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot delete the currently checked-out branch.");
        repo.Branches.Remove(branchName);
    }

    public void RenameBranch(string path, string oldName, string newName)
    {
        using var repo = new Repository(path);
        var branch = repo.Branches[oldName] ?? throw new ArgumentException($"Branch '{oldName}' not found");
        repo.Branches.Rename(branch, newName);
    }

    // ------------------------------------------------------------ Staging

    public void Stage(string path, params string[] filePaths)
    {
        using var repo = new Repository(path);
        Commands.Stage(repo, filePaths);
    }

    public void StageAll(string path)
    {
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
    }

    public void Unstage(string path, params string[] filePaths)
    {
        using var repo = new Repository(path);
        Commands.Unstage(repo, filePaths);
    }

    // ------------------------------------------------------------ Commit / amend / reset

    public void Commit(string path, string message, string authorName, string authorEmail)
    {
        using var repo = new Repository(path);
        var sig = new Signature(authorName, authorEmail, DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    /// <summary>Amend the tip commit. Pass null for any argument to keep the existing value.</summary>
    public void AmendCommit(string path, string? message = null, string? authorName = null, string? authorEmail = null)
    {
        using var repo = new Repository(path);
        var tip = repo.Head.Tip ?? throw new InvalidOperationException("No commit to amend.");
        var sig = authorName is not null && authorEmail is not null
            ? new Signature(authorName, authorEmail, DateTimeOffset.Now)
            : GetSignatureOrDefault(repo);
        repo.Commit(message ?? tip.Message, sig, sig, new CommitOptions { AmendPreviousCommit = true });
    }

    public void Reset(string path, ResetMode mode, string refName = "HEAD")
    {
        using var repo = new Repository(path);
        var commit = repo.Lookup<Commit>(refName)
            ?? repo.Branches[refName]?.Tip
            ?? throw new ArgumentException($"Ref '{refName}' not found");
        repo.Reset(MapResetMode(mode), commit);
    }

    // ------------------------------------------------------------ Discard

    /// <summary>Reverts working-tree + index for a single tracked file to its HEAD content.
    /// For untracked files, prefer deleting from disk (with recycle bin fallback in the UI).</summary>
    public void DiscardFile(string path, string filePath)
    {
        using var repo = new Repository(path);
        repo.CheckoutPaths("HEAD", new[] { filePath }, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force,
        });
    }

    public void DiscardAll(string path)
    {
        using var repo = new Repository(path);
        repo.Reset(LibGit2Sharp.ResetMode.Hard, repo.Head.Tip);
    }

    // ------------------------------------------------------------ Stash

    public IReadOnlyList<GitStashEntry> StashList(string path)
    {
        using var repo = new Repository(path);
        return repo.Stashes
            .Select((s, i) => new GitStashEntry(i, s.WorkTree.Sha, s.Message, s.WorkTree.Author.When))
            .ToList();
    }

    public void Stash(string path, string? message = null)
    {
        using var repo = new Repository(path);
        var sig = GetSignatureOrDefault(repo);
        repo.Stashes.Add(sig, message, StashModifiers.IncludeUntracked);
    }

    public void StashPop(string path, int index = 0)
    {
        using var repo = new Repository(path);
        repo.Stashes.Pop(index);
    }

    public void StashApply(string path, int index = 0)
    {
        using var repo = new Repository(path);
        repo.Stashes.Apply(index);
    }

    public void StashDrop(string path, int index)
    {
        using var repo = new Repository(path);
        repo.Stashes.Remove(index);
    }

    // ------------------------------------------------------------ Conflicts

    public IReadOnlyList<string> ConflictedPaths(string path)
    {
        using var repo = new Repository(path);
        return repo.Index.Conflicts.Select(c => c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor?.Path ?? "").ToList();
    }

    /// <summary>Resolves a conflicted path by writing the chosen side's blob to disk and staging it.</summary>
    public void ResolveAs(string path, string filePath, ConflictSide side)
    {
        using var repo = new Repository(path);
        var conflict = repo.Index.Conflicts.FirstOrDefault(c =>
            string.Equals(c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor?.Path, filePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No conflict for '{filePath}'.");
        var entry = side == ConflictSide.Ours ? conflict.Ours : conflict.Theirs;
        if (entry is null) throw new InvalidOperationException($"Conflict has no '{side}' side.");
        var blob = repo.Lookup<Blob>(entry.Id) ?? throw new InvalidOperationException("Blob not found.");
        var absPath = Path.Combine(repo.Info.WorkingDirectory, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        using (var fs = File.Create(absPath))
        using (var src = blob.GetContentStream())
            src.CopyTo(fs);
        Commands.Stage(repo, filePath);
    }

    public ConflictVersions? GetConflictVersions(string path, string filePath)
    {
        using var repo = new Repository(path);
        var conflict = repo.Index.Conflicts.FirstOrDefault(c =>
            string.Equals(c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor?.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (conflict is null) return null;
        return new ConflictVersions(
            BaseText: BlobText(repo, conflict.Ancestor?.Id),
            OursText: BlobText(repo, conflict.Ours?.Id),
            TheirsText: BlobText(repo, conflict.Theirs?.Id));
    }

    // ------------------------------------------------------------ Config / signature

    /// <summary>Reads user.name / user.email from local→global→system config (libgit2 walks
    /// the chain). Returns null when both are missing — the UI surfaces this as a warning.</summary>
    public Signature? GetSignature(string path)
    {
        using var repo = new Repository(path);
        return repo.Config.BuildSignature(DateTimeOffset.Now);
    }

    public (string? Name, string? Email) GetIdentity(string path)
    {
        using var repo = new Repository(path);
        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;
        return (name, email);
    }

    public void SetIdentity(string path, string name, string email, bool global = false)
    {
        using var repo = new Repository(path);
        var level = global ? ConfigurationLevel.Global : ConfigurationLevel.Local;
        repo.Config.Set("user.name", name, level);
        repo.Config.Set("user.email", email, level);
    }

    // ------------------------------------------------------------ Remote tracking

    public (string Remote, string Branch)? GetUpstream(string path, string branchName)
    {
        using var repo = new Repository(path);
        var branch = repo.Branches[branchName];
        if (branch?.TrackedBranch is null) return null;
        var tracked = branch.TrackedBranch;
        return (tracked.RemoteName ?? "origin", tracked.FriendlyName.Replace($"{tracked.RemoteName}/", ""));
    }

    public (int Ahead, int Behind) AheadBehind(string path, string branchName)
    {
        using var repo = new Repository(path);
        var branch = repo.Branches[branchName];
        if (branch?.TrackingDetails is null) return (0, 0);
        return (branch.TrackingDetails.AheadBy ?? 0, branch.TrackingDetails.BehindBy ?? 0);
    }

    public void SetUpstream(string path, string branchName, string remote, string remoteBranch)
    {
        using var repo = new Repository(path);
        var branch = repo.Branches[branchName] ?? throw new ArgumentException($"Branch '{branchName}' not found");
        repo.Branches.Update(branch,
            b => b.Remote = remote,
            b => b.UpstreamBranch = $"refs/heads/{remoteBranch}");
    }

    // ------------------------------------------------------------ Commit history

    public IReadOnlyList<GitCommitSummary> RecentCommits(string path, int count = 50)
    {
        using var repo = new Repository(path);
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Time };
        return repo.Commits.QueryBy(filter)
            .Take(count)
            .Select(c => new GitCommitSummary(c.Sha, c.MessageShort, c.Author.Name, c.Author.When))
            .ToList();
    }

    // ------------------------------------------------------------ Diff (legacy text)

    /// <summary>Returns the diff text for unstaged changes (HEAD → working dir).</summary>
    public string UnstagedDiff(string path)
    {
        using var repo = new Repository(path);
        var changes = repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.WorkingDirectory);
        return changes.Content;
    }

    /// <summary>Returns the diff text for staged changes (HEAD → index).</summary>
    public string StagedDiff(string path)
    {
        using var repo = new Repository(path);
        var changes = repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.Index);
        return changes.Content;
    }

    // ------------------------------------------------------------ Diff (structured)

    /// <summary>Returns per-file diffs between two sources, with hunks parsed into structured
    /// rows for the side-by-side diff tab. Rename detection is enabled.</summary>
    public IReadOnlyList<GitFileDiff> FileDiffs(string path, DiffSource baseRef, DiffSource headRef, string? filterToFile = null)
    {
        using var repo = new Repository(path);
        var paths = filterToFile is null ? null : new[] { filterToFile };

        Patch patch = (baseRef.Kind, headRef.Kind) switch
        {
            (DiffSource.Source.Head, DiffSource.Source.Index) =>
                repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.Index, paths, null, RenameAware),
            (DiffSource.Source.Head, DiffSource.Source.WorkingTree) =>
                repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.WorkingDirectory, paths, null, RenameAware),
            (DiffSource.Source.Index, DiffSource.Source.WorkingTree) =>
                repo.Diff.Compare<Patch>(paths, includeUntracked: true, explicitPathsOptions: null, compareOptions: RenameAware),
            (DiffSource.Source.Reference, DiffSource.Source.Reference) =>
                repo.Diff.Compare<Patch>(LookupTree(repo, baseRef.Reference!), LookupTree(repo, headRef.Reference!), paths, null, RenameAware),
            (DiffSource.Source.Reference, DiffSource.Source.WorkingTree) =>
                repo.Diff.Compare<Patch>(LookupTree(repo, baseRef.Reference!), DiffTargets.WorkingDirectory, paths, null, RenameAware),
            _ => throw new NotSupportedException($"Diff source pair {baseRef.Kind}→{headRef.Kind} is not supported."),
        };

        var result = new List<GitFileDiff>();
        foreach (var entry in patch)
        {
            var hunks = entry.IsBinaryComparison
                ? Array.Empty<GitDiffHunk>()
                : UnifiedDiffParser.ParseHunks(entry.Patch);
            result.Add(new GitFileDiff(
                Path: entry.Path,
                OldPath: string.Equals(entry.OldPath, entry.Path, StringComparison.Ordinal) ? null : entry.OldPath,
                Kind: MapChangeKind(entry.Status),
                IsBinary: entry.IsBinaryComparison,
                AdditionCount: entry.LinesAdded,
                DeletionCount: entry.LinesDeleted,
                Hunks: hunks));
        }
        return result;
    }

    // ------------------------------------------------------------ File content / blob info

    /// <summary>Returns text content at a given ref ("HEAD", "INDEX", "WORKDIR", or a
    /// commit / branch name). Returns null when the blob is binary or the path is missing.</summary>
    public string? GetFileAt(string path, string refName, string filePath)
    {
        using var repo = new Repository(path);
        switch (refName.ToUpperInvariant())
        {
            case "WORKDIR":
            case "WORKINGTREE":
                var abs = Path.Combine(repo.Info.WorkingDirectory, filePath);
                return File.Exists(abs) ? File.ReadAllText(abs) : null;
            case "INDEX":
                var entry = repo.Index[filePath];
                if (entry is null) return null;
                var indexBlob = repo.Lookup<Blob>(entry.Id);
                return BlobText(repo, indexBlob);
            default:
                var commit = repo.Lookup<Commit>(refName) ?? repo.Branches[refName]?.Tip;
                if (commit is null) return null;
                var treeEntry = commit[filePath];
                if (treeEntry?.Target is not Blob blob) return null;
                return BlobText(repo, blob);
        }
    }

    public (long Size, bool IsBinary)? GetBlobInfo(string path, string refName, string filePath)
    {
        using var repo = new Repository(path);
        Blob? blob = refName.ToUpperInvariant() switch
        {
            "INDEX" => repo.Index[filePath] is { } ie ? repo.Lookup<Blob>(ie.Id) : null,
            "WORKDIR" or "WORKINGTREE" => null,
            _ => (repo.Lookup<Commit>(refName) ?? repo.Branches[refName]?.Tip)?[filePath]?.Target as Blob,
        };
        if (blob is not null) return (blob.Size, blob.IsBinary);
        if (refName.Equals("WORKDIR", StringComparison.OrdinalIgnoreCase) ||
            refName.Equals("WORKINGTREE", StringComparison.OrdinalIgnoreCase))
        {
            var abs = Path.Combine(repo.Info.WorkingDirectory, filePath);
            if (!File.Exists(abs)) return null;
            var info = new FileInfo(abs);
            return (info.Length, IsBinaryFile(abs));
        }
        return null;
    }

    // ------------------------------------------------------------ Helpers

    private static Tree LookupTree(Repository repo, string refName)
    {
        var commit = repo.Lookup<Commit>(refName) ?? repo.Branches[refName]?.Tip
            ?? throw new ArgumentException($"Ref '{refName}' not found");
        return commit.Tree;
    }

    private static string? BlobText(Repository repo, ObjectId? id) =>
        id is null ? null : BlobText(repo, repo.Lookup<Blob>(id));

    private static string? BlobText(Repository repo, Blob? blob)
    {
        if (blob is null || blob.IsBinary) return null;
        using var stream = blob.GetContentStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Signature GetSignatureOrDefault(Repository repo) =>
        repo.Config.BuildSignature(DateTimeOffset.Now)
        ?? new Signature("Vegha user", "user@vegha.local", DateTimeOffset.Now);

    private static bool IsBinaryFile(string absPath)
    {
        // Cheap heuristic: peek first 8KB for NUL byte. Matches what git does.
        Span<byte> buf = stackalloc byte[8192];
        using var fs = File.OpenRead(absPath);
        var read = fs.Read(buf);
        for (int i = 0; i < read; i++) if (buf[i] == 0) return true;
        return false;
    }

    private static GitChangeKind MapStatus(FileStatus s)
    {
        if (s.HasFlag(FileStatus.Conflicted)) return GitChangeKind.Conflict;
        if (s.HasFlag(FileStatus.NewInIndex) || s.HasFlag(FileStatus.NewInWorkdir)) return GitChangeKind.Added;
        if (s.HasFlag(FileStatus.DeletedFromIndex) || s.HasFlag(FileStatus.DeletedFromWorkdir)) return GitChangeKind.Deleted;
        if (s.HasFlag(FileStatus.ModifiedInIndex) || s.HasFlag(FileStatus.ModifiedInWorkdir)) return GitChangeKind.Modified;
        if (s.HasFlag(FileStatus.RenamedInIndex) || s.HasFlag(FileStatus.RenamedInWorkdir)) return GitChangeKind.Renamed;
        return GitChangeKind.Unknown;
    }

    private static GitChangeKind MapChangeKind(ChangeKind k) => k switch
    {
        ChangeKind.Added => GitChangeKind.Added,
        ChangeKind.Deleted => GitChangeKind.Deleted,
        ChangeKind.Modified => GitChangeKind.Modified,
        ChangeKind.Renamed => GitChangeKind.Renamed,
        ChangeKind.Copied => GitChangeKind.Renamed,
        ChangeKind.Conflicted => GitChangeKind.Conflict,
        _ => GitChangeKind.Unknown,
    };

    private static LibGit2Sharp.ResetMode MapResetMode(ResetMode mode) => mode switch
    {
        ResetMode.Soft => LibGit2Sharp.ResetMode.Soft,
        ResetMode.Mixed => LibGit2Sharp.ResetMode.Mixed,
        ResetMode.Hard => LibGit2Sharp.ResetMode.Hard,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static bool IsStaged(FileStatus s) =>
        s.HasFlag(FileStatus.NewInIndex) ||
        s.HasFlag(FileStatus.ModifiedInIndex) ||
        s.HasFlag(FileStatus.DeletedFromIndex) ||
        s.HasFlag(FileStatus.RenamedInIndex);
}

public enum GitChangeKind
{
    Unknown,
    Added,
    Modified,
    Deleted,
    Renamed,
    Conflict,
}

public enum ResetMode { Soft, Mixed, Hard }

public enum ConflictSide { Ours, Theirs }

public sealed record GitFileChange(string Path, GitChangeKind Kind, bool IsStaged);

public sealed record GitCommitSummary(string Sha, string MessageShort, string AuthorName, DateTimeOffset When);

public sealed record GitStashEntry(int Index, string Sha, string Message, DateTimeOffset When);

public sealed record GitRemoteEntry(string Name, string Url, string PushUrl);

public sealed record ConflictVersions(string? BaseText, string? OursText, string? TheirsText);

/// <summary>One side of a diff request — a fixed source (HEAD / Index / WorkingTree) or a
/// named reference (branch / commit sha).</summary>
public readonly record struct DiffSource(DiffSource.Source Kind, string? Reference)
{
    public enum Source { Head, Index, WorkingTree, Reference }

    public static readonly DiffSource Head = new(Source.Head, null);
    public static readonly DiffSource Index = new(Source.Index, null);
    public static readonly DiffSource WorkingTree = new(Source.WorkingTree, null);
    public static DiffSource Ref(string name) => new(Source.Reference, name);
}
