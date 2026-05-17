using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Vegha.Integrations.Git;

/// <summary>Async network operations (fetch / pull / push / clone). Prefers shelling out to
/// <c>git.exe</c> via <see cref="GitProcessRunner"/> when available — that path inherits Git
/// Credential Manager, SSH config, HTTP proxies, and signed-push behavior. Falls back to
/// libgit2sharp's HTTP transport with a <see cref="GitCredentialsService"/> when git isn't on
/// PATH.</summary>
public sealed partial class GitService
{
    public async Task FetchAsync(string path, string remote = "origin", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_runner.IsAvailable)
        {
            var result = await _runner.RunAsync(path, new[] { "fetch", remote, "--prune" }, stdin: null, ct).ConfigureAwait(false);
            progress?.Report(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            if (!result.Success)
                throw new GitOperationException("fetch", result.ExitCode, result.StdErr);
            return;
        }
        await Task.Run(() => FetchSync(path, remote, progress), ct).ConfigureAwait(false);
    }

    public async Task PullAsync(string path, string remote = "origin", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_runner.IsAvailable)
        {
            var result = await _runner.RunAsync(path, new[] { "pull", remote, "--ff-only" }, stdin: null, ct).ConfigureAwait(false);
            progress?.Report(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            if (!result.Success)
                throw new GitOperationException("pull", result.ExitCode, result.StdErr);
            return;
        }
        await Task.Run(() => PullSync(path, remote, progress), ct).ConfigureAwait(false);
    }

    public async Task PushAsync(string path, string remote = "origin", string? branch = null, bool setUpstream = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        branch ??= CurrentBranch(path);
        if (_runner.IsAvailable)
        {
            var args = new List<string> { "push" };
            if (setUpstream) args.Add("--set-upstream");
            args.Add(remote);
            args.Add(branch);
            var result = await _runner.RunAsync(path, args, stdin: null, ct).ConfigureAwait(false);
            progress?.Report(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            if (!result.Success)
                throw new GitOperationException("push", result.ExitCode, result.StdErr);
            return;
        }
        await Task.Run(() => PushSync(path, remote, branch, setUpstream, progress), ct).ConfigureAwait(false);
    }

    public async Task CloneAsync(string url, string targetDir, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        if (_runner.IsAvailable)
        {
            var result = await _runner.RunAsync(Path.GetDirectoryName(targetDir) ?? ".",
                new[] { "clone", url, Path.GetFileName(targetDir) }, stdin: null, ct).ConfigureAwait(false);
            progress?.Report(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            if (!result.Success)
                throw new GitOperationException("clone", result.ExitCode, result.StdErr);
            return;
        }
        await Task.Run(() => Repository.Clone(url, targetDir, new CloneOptions
        {
            FetchOptions = { CredentialsProvider = BuildCredentialsProvider(url) },
        }), ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------- libgit2sharp fallbacks

    private void FetchSync(string path, string remote, IProgress<string>? progress)
    {
        using var repo = new Repository(path);
        var r = repo.Network.Remotes[remote] ?? throw new ArgumentException($"Remote '{remote}' not found");
        var refSpecs = r.FetchRefSpecs.Select(s => s.Specification);
        Commands.Fetch(repo, r.Name, refSpecs, BuildFetchOptions(r.Url, progress), logMessage: null);
    }

    private void PullSync(string path, string remote, IProgress<string>? progress)
    {
        using var repo = new Repository(path);
        var sig = GetSignatureOrDefault(repo);
        var options = new PullOptions
        {
            FetchOptions = BuildFetchOptions(repo.Network.Remotes[remote]?.Url ?? remote, progress),
            MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly },
        };
        Commands.Pull(repo, sig, options);
    }

    private void PushSync(string path, string remote, string branch, bool setUpstream, IProgress<string>? progress)
    {
        using var repo = new Repository(path);
        var r = repo.Network.Remotes[remote] ?? throw new ArgumentException($"Remote '{remote}' not found");
        var branchObj = repo.Branches[branch] ?? throw new ArgumentException($"Branch '{branch}' not found");
        if (setUpstream)
            repo.Branches.Update(branchObj, b => b.Remote = remote, b => b.UpstreamBranch = $"refs/heads/{branch}");

        var pushOptions = new PushOptions { CredentialsProvider = BuildCredentialsProvider(r.Url) };
        if (progress is not null)
            pushOptions.OnPushStatusError = err => progress.Report($"{err.Reference}: {err.Message}");
        repo.Network.Push(branchObj, pushOptions);
    }

    private FetchOptions BuildFetchOptions(string url, IProgress<string>? progress)
    {
        var options = new FetchOptions { CredentialsProvider = BuildCredentialsProvider(url) };
        if (progress is not null)
            options.OnProgress = msg => { progress.Report(msg); return true; };
        return options;
    }

    private CredentialsHandler? BuildCredentialsProvider(string url) =>
        _credentials is null
            ? null
            : (_, usernameHint, _) =>
              {
                  if (!_credentials.TryGet(url, usernameHint, out var user, out var secret))
                      throw new GitOperationException("credentials", -1, $"No credentials available for {url}.");
                  return new UsernamePasswordCredentials { Username = user, Password = secret };
              };
}

public sealed class GitOperationException : Exception
{
    public string Operation { get; }
    public int ExitCode { get; }
    public GitOperationException(string operation, int exitCode, string message)
        : base($"git {operation} failed (exit {exitCode}): {message.Trim()}")
    {
        Operation = operation;
        ExitCode = exitCode;
    }
}
