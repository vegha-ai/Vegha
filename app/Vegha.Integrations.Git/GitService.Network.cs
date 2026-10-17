using LibGit2Sharp;

namespace Vegha.Integrations.Git;

/// <summary>Network + repo-lifecycle operations. Everything that talks to a remote shells out
/// to the git CLI via <see cref="GitProcessRunner"/> — that path inherits Git Credential
/// Manager (browser SSO / MFA / WAM), the SSH agent, ~/.ssh/config, proxies and enterprise
/// gitconfig. Per-operation credentials / proxy are layered on by the
/// <see cref="IGitEnvironmentProvider"/> (see <see cref="GitCredentialBroker"/>).
///
/// Remote names are never assumed: <see cref="ResolveRemote"/> picks the remote the same way
/// git does (pushRemote → upstream → remote.pushDefault → the only remote → origin) and
/// throws a typed <see cref="GitErrorKind.NoRemoteConfigured"/> when there is none.</summary>
public sealed partial class GitService
{
    /// <summary>Default network timeout (generous: a first-time GCM browser sign-in with MFA
    /// can easily take a couple of minutes).</summary>
    public TimeSpan NetworkTimeout { get; set; } = TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------ Remote resolution

    /// <summary>Picks the remote to use for <paramref name="branch"/>. For push:
    /// <c>branch.&lt;b&gt;.pushRemote</c> → <c>remote.pushDefault</c> → upstream remote; for
    /// fetch/pull: upstream remote. Then: the only remote, then <c>origin</c> if it exists.
    /// Returns null when the repository has no remotes at all.</summary>
    public string? ResolveRemote(string path, string? branch, bool forPush)
    {
        using var repo = new Repository(path);
        var names = repo.Network.Remotes.Select(r => r.Name).ToList();
        if (names.Count == 0) return null;

        bool Exists(string? n) => !string.IsNullOrEmpty(n) && names.Contains(n!, StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(branch))
        {
            if (forPush)
            {
                var pushRemote = repo.Config.Get<string>($"branch.{branch}.pushRemote")?.Value;
                if (Exists(pushRemote)) return pushRemote;
                var pushDefault = repo.Config.Get<string>("remote.pushDefault")?.Value;
                if (Exists(pushDefault)) return pushDefault;
            }
            var upstreamRemote = repo.Config.Get<string>($"branch.{branch}.remote")?.Value;
            if (Exists(upstreamRemote)) return upstreamRemote;
        }

        if (names.Count == 1) return names[0];
        if (Exists("origin")) return "origin";
        return null;
    }

    /// <summary>URL of a remote (push URL when <paramref name="forPush"/>), or null.</summary>
    public string? RemoteUrl(string path, string remote, bool forPush = false)
    {
        using var repo = new Repository(path);
        var r = repo.Network.Remotes[remote];
        if (r is null) return null;
        return forPush && !string.IsNullOrEmpty(r.PushUrl) ? r.PushUrl : r.Url;
    }

    /// <summary>True when the repository has more than one remote and the branch has no
    /// upstream / pushRemote — the UI should ask which remote to publish to.</summary>
    public bool NeedsRemoteChoice(string path, string branch)
    {
        using var repo = new Repository(path);
        if (repo.Network.Remotes.Count() <= 1) return false;
        return repo.Config.Get<string>($"branch.{branch}.remote") is null
            && repo.Config.Get<string>($"branch.{branch}.pushRemote") is null
            && repo.Config.Get<string>("remote.pushDefault") is null
            && repo.Network.Remotes["origin"] is null;
    }

    // ------------------------------------------------------------ Fetch / pull / push

    /// <summary>Fetches every remote (<c>--all --prune</c>), or just <paramref name="remote"/>.</summary>
    public async Task FetchAsync(string path, string? remote = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var remotes = Remotes(path);
        if (remotes.Count == 0) throw NoRemote("fetch");

        var args = new List<string> { "fetch", "--prune", "--progress" };
        string url;
        if (string.IsNullOrEmpty(remote))
        {
            args.Add("--all");
            url = remotes[0].Url;
        }
        else
        {
            args.Add(remote);
            url = remotes.FirstOrDefault(r => r.Name == remote)?.Url ?? throw RemoteMissing("fetch", remote);
        }
        await RunNetworkAsync("fetch", path, url, args, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Pulls the current branch's upstream using <paramref name="mode"/>. When the
    /// branch has no upstream but a same-named branch exists on the resolved remote, pulls that
    /// and sets it as upstream.</summary>
    public async Task PullAsync(string path, GitPullMode mode = GitPullMode.Rebase, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var branch = CurrentBranch(path);
        var remote = ResolveRemote(path, branch, forPush: false) ?? throw NoRemote("pull");
        var url = RemoteUrl(path, remote) ?? throw RemoteMissing("pull", remote);

        var upstream = GetUpstream(path, branch);
        var remoteBranch = upstream?.Branch ?? branch;

        var args = new List<string> { "pull", "--progress" };
        switch (mode)
        {
            case GitPullMode.Rebase: args.Add("--rebase"); args.Add("--autostash"); break;
            case GitPullMode.Merge: args.Add("--no-rebase"); args.Add("--autostash"); break;
            default: args.Add("--ff-only"); break;
        }
        args.Add(upstream?.Remote ?? remote);
        args.Add(remoteBranch);
        await RunNetworkAsync("pull", path, url, args, progress, ct).ConfigureAwait(false);

        if (upstream is null)
        {
            try { SetUpstream(path, branch, remote, remoteBranch); } catch { /* remote branch may not exist */ }
        }
    }

    /// <summary>Pushes the current (or given) branch. Sets upstream automatically on first push
    /// (<c>push -u &lt;remote&gt; HEAD:refs/heads/&lt;branch&gt;</c>).</summary>
    public async Task PushAsync(string path, string? remote = null, string? branch = null, bool setUpstream = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        branch ??= CurrentBranch(path);
        if (IsDetachedHead(path)) throw new GitOperationException("push", -1, "HEAD is detached — check out a branch before pushing.", GitErrorKind.Unknown);
        remote ??= ResolveRemote(path, branch, forPush: true) ?? throw NoRemote("push");
        var url = RemoteUrl(path, remote, forPush: true) ?? throw RemoteMissing("push", remote);

        var upstream = GetUpstream(path, branch);
        var args = new List<string> { "push", "--progress" };
        if (setUpstream || upstream is null) args.Add("--set-upstream");
        args.Add(remote);
        var targetBranch = upstream is { } u && u.Remote == remote ? u.Branch : branch;
        args.Add($"HEAD:refs/heads/{targetBranch}");
        await RunNetworkAsync("push", path, url, args, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="targetDir"/> (must not exist
    /// or be empty). <paramref name="partial"/> uses a blobless partial clone — much faster for
    /// large monorepos where only a few collections are needed.</summary>
    public async Task CloneAsync(string url, string targetDir, string? branch = null, bool partial = false,
        IProgress<string>? progress = null, CancellationToken ct = default, GitEnvironment? credentials = null)
    {
        // No sparse checkout here on purpose: it enables extensions.worktreeConfig, which
        // libgit2 (used for status / stage / commit) can't open — and libgit2 ignores sparse
        // patterns, so files outside the cone would show up as deleted.
        var parent = Path.GetDirectoryName(Path.GetFullPath(targetDir)) ?? ".";
        Directory.CreateDirectory(parent);
        var args = new List<string> { "clone", "--progress" };
        if (!string.IsNullOrEmpty(branch)) { args.Add("--branch"); args.Add(branch); }
        if (partial) args.Add("--filter=blob:none");
        args.Add("--");
        args.Add(url);
        args.Add(Path.GetFullPath(targetDir));
        try
        {
            await RunNetworkAsync("clone", parent, url, args, progress, ct, credentials).ConfigureAwait(false);
        }
        catch
        {
            // Leave no half-cloned folder behind so a retry doesn't fail with "already exists".
            TryDeleteDirectory(targetDir);
            throw;
        }
    }

    /// <summary>Lists remote branches + the default branch without cloning (<c>ls-remote
    /// --symref</c>). Also the cheapest "can I authenticate?" probe.</summary>
    public async Task<GitRemoteInfo> LsRemoteAsync(string url, IProgress<string>? progress = null, CancellationToken ct = default, string? repoRoot = null,
        GitEnvironment? credentials = null)
    {
        var args = new List<string> { "ls-remote", "--symref", "--", url };
        var result = await RunNetworkAsync("ls-remote", repoRoot ?? Path.GetTempPath(), url, args, progress, ct, credentials).ConfigureAwait(false);
        string? defaultBranch = null;
        var branches = new List<string>();
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal) && line.EndsWith("\tHEAD", StringComparison.Ordinal))
                defaultBranch = line["ref: refs/heads/".Length..^"\tHEAD".Length];
            else
            {
                var tab = line.IndexOf('\t');
                if (tab > 0 && line[(tab + 1)..].StartsWith("refs/heads/", StringComparison.Ordinal))
                    branches.Add(line[(tab + 1 + "refs/heads/".Length)..]);
            }
        }
        return new GitRemoteInfo(defaultBranch, branches);
    }

    /// <summary>Switches branches with the git CLI. Unlike libgit2 this works in partial
    /// (blobless) clones — missing file contents are fetched on demand, using the same
    /// credentials as fetch — and it honors hooks, LFS and the user's checkout config.
    /// A remote-only branch name ("feature") is created as a tracking branch automatically.</summary>
    public async Task CheckoutAsync(string path, string branchName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var remote = ResolveRemote(path, CurrentBranch(path), forPush: false);
        var url = remote is null ? null : RemoteUrl(path, remote);
        var args = new[] { "switch", branchName };
        if (url is null)
        {
            var r = await _runner.RunAsync(path, args, ct: ct).ConfigureAwait(false);
            if (!r.Success) throw Failed("checkout", r);
            return;
        }
        await RunNetworkAsync("checkout", path, url, args, progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ Init / publish helpers

    /// <summary>Initializes a repository with <paramref name="initialBranch"/> (or the user's
    /// <c>init.defaultBranch</c>, falling back to <c>main</c>).</summary>
    public async Task InitAsync(string path, string? initialBranch = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);
        if (_runner.IsAvailable)
        {
            var branch = initialBranch;
            if (string.IsNullOrEmpty(branch))
            {
                var cfg = await _runner.RunAsync(path, new[] { "config", "--get", "init.defaultBranch" }, ct: ct).ConfigureAwait(false);
                branch = cfg.Success && !string.IsNullOrWhiteSpace(cfg.StdOut) ? cfg.StdOut.Trim() : "main";
            }
            var result = await _runner.RunAsync(path, new[] { "init", "-b", branch }, ct: ct).ConfigureAwait(false);
            if (!result.Success) throw Failed("init", result);
            return;
        }
        Repository.Init(path);
    }

    // ------------------------------------------------------------ In-progress operations

    /// <summary>Which multi-step operation (rebase / merge / cherry-pick / revert) is paused
    /// waiting for the user, if any.</summary>
    public GitRepoOperation CurrentOperation(string path)
    {
        using var repo = new Repository(path);
        return repo.Info.CurrentOperation switch
        {
            LibGit2Sharp.CurrentOperation.None => GitRepoOperation.None,
            LibGit2Sharp.CurrentOperation.Merge => GitRepoOperation.Merge,
            LibGit2Sharp.CurrentOperation.Revert or LibGit2Sharp.CurrentOperation.RevertSequence => GitRepoOperation.Revert,
            LibGit2Sharp.CurrentOperation.CherryPick or LibGit2Sharp.CurrentOperation.CherryPickSequence => GitRepoOperation.CherryPick,
            _ => GitRepoOperation.Rebase,
        };
    }

    /// <summary>Continues / aborts / skips the paused operation via the git CLI.</summary>
    public async Task ControlOperationAsync(string path, GitOperationControl control, CancellationToken ct = default)
    {
        var op = CurrentOperation(path);
        if (op == GitRepoOperation.None) return;
        var verb = op switch
        {
            GitRepoOperation.Merge => "merge",
            GitRepoOperation.CherryPick => "cherry-pick",
            GitRepoOperation.Revert => "revert",
            _ => "rebase",
        };
        var flag = control switch
        {
            GitOperationControl.Abort => "--abort",
            GitOperationControl.Skip => "--skip",
            _ => "--continue",
        };
        // Continue would open an editor for the commit message — accept the prepared one.
        var env = new GitEnvironment().Set("GIT_EDITOR", "true").Set("GIT_SEQUENCE_EDITOR", "true");
        var result = await _runner.RunAsync(path, new[] { verb, flag }, new GitRunOptions { Environment = env }, ct).ConfigureAwait(false);
        if (!result.Success) throw Failed(verb, result);
    }

    public bool IsDetachedHead(string path)
    {
        using var repo = new Repository(path);
        return repo.Info.IsHeadDetached;
    }

    public bool IsUnborn(string path)
    {
        using var repo = new Repository(path);
        return repo.Info.IsHeadUnborn;
    }

    // ------------------------------------------------------------ Plumbing

    private async Task<GitProcessRunner.Result> RunNetworkAsync(string op, string workingDir, string url, IReadOnlyList<string> args,
        IProgress<string>? progress, CancellationToken ct, GitEnvironment? extra = null)
    {
        if (!_runner.IsAvailable)
            throw new GitOperationException(op, -1, "Git is not installed.", GitErrorKind.GitNotInstalled);

        GitEnvironment? env = null;
        if (_environmentProvider is not null)
            env = await _environmentProvider.BuildAsync(new GitEnvironmentRequest(op, url, workingDir), ct).ConfigureAwait(false);
        // Explicit per-call credentials (e.g. a PAT typed into a dialog) are applied last so they win.
        if (extra is not null) env = (env ?? new GitEnvironment()).Merge(extra);

        var result = await _runner.RunAsync(workingDir, args, new GitRunOptions
        {
            Environment = env,
            OnProgress = progress is null ? null : progress.Report,
            Timeout = NetworkTimeout,
        }, ct).ConfigureAwait(false);

        if (!result.Success) throw Failed(op, result);
        if (progress is not null && !string.IsNullOrWhiteSpace(result.StdOut) && op != "ls-remote")
            progress.Report(result.StdOut.TrimEnd());
        return result;
    }

    private static GitOperationException Failed(string op, GitProcessRunner.Result result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var info = GitErrorClassifier.Classify(text, op);
        return new GitOperationException(op, result.ExitCode, text, info);
    }

    private static GitOperationException NoRemote(string op) =>
        new(op, -1, "This repository has no remote configured.", GitErrorClassifier.NoRemote(op));

    private static GitOperationException RemoteMissing(string op, string remote) =>
        new(op, -1, $"'{remote}' does not appear to be a git repository",
            GitErrorClassifier.Classify($"fatal: '{remote}' does not appear to be a git repository", op));

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }
}

/// <summary>How <see cref="GitService.PullAsync"/> integrates divergent history.</summary>
public enum GitPullMode { Rebase, Merge, FastForwardOnly }

public enum GitRepoOperation { None, Merge, Rebase, CherryPick, Revert }

public enum GitOperationControl { Continue, Abort, Skip }

/// <summary>Result of <see cref="GitService.LsRemoteAsync"/>.</summary>
public sealed record GitRemoteInfo(string? DefaultBranch, IReadOnlyList<string> Branches);

/// <summary>Context handed to an <see cref="IGitEnvironmentProvider"/> for one network op.</summary>
public sealed record GitEnvironmentRequest(string Operation, string RemoteUrl, string WorkingDirectory);

/// <summary>Supplies per-operation env/config (credentials, proxy, ssh command).</summary>
public interface IGitEnvironmentProvider
{
    Task<GitEnvironment> BuildAsync(GitEnvironmentRequest request, CancellationToken ct);
}

public sealed class GitOperationException : Exception
{
    public string Operation { get; }
    public int ExitCode { get; }
    public GitErrorInfo Error { get; }
    public GitErrorKind Kind => Error.Kind;

    /// <summary>Raw git stderr/stdout (for the Git Output panel).</summary>
    public string RawOutput { get; }

    public GitOperationException(string operation, int exitCode, string message)
        : this(operation, exitCode, message, GitErrorClassifier.Classify(message, operation)) { }

    public GitOperationException(string operation, int exitCode, string message, GitErrorKind kind)
        : this(operation, exitCode, message, kind switch
        {
            GitErrorKind.GitNotInstalled => GitErrorClassifier.NotInstalled(),
            GitErrorKind.NoRemoteConfigured => GitErrorClassifier.NoRemote(operation),
            GitErrorKind.Timeout => new GitErrorInfo(GitErrorKind.Timeout, "Git timed out",
                "The operation took too long. If a sign-in window is open, finish signing in and retry.",
                new[] { GitErrorAction.OpenDiagnostics }),
            _ => GitErrorClassifier.Classify(message, operation),
        }) { }

    public GitOperationException(string operation, int exitCode, string message, GitErrorInfo info)
        : base($"git {operation} failed (exit {exitCode}): {message.Trim()}")
    {
        Operation = operation;
        ExitCode = exitCode;
        Error = info;
        RawOutput = message;
    }
}
