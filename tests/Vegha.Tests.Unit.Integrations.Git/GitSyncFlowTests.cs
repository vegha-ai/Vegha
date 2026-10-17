using FluentAssertions;
using Vegha.Integrations.Git;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

/// <summary>End-to-end sync flows against local bare repositories over the git CLI: repo
/// discovery from a collection subfolder, remote resolution without assuming "origin", first
/// push setting upstream, pull modes on diverged history, and typed errors. Skips silently
/// when git isn't installed (same policy as <see cref="GitProcessRunnerTests"/>).</summary>
public sealed class GitSyncFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vegha-sync-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git;
    private readonly bool _available;

    public GitSyncFlowTests()
    {
        Directory.CreateDirectory(_root);
        var runner = new GitProcessRunner();
        _available = runner.IsAvailable;
        _git = new GitService(runner, new IsolatedConfigProvider(_root));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Keeps the developer's global gitconfig (signing, hooks, aliases) out of tests.</summary>
    private sealed class IsolatedConfigProvider : IGitEnvironmentProvider
    {
        private readonly string _globalConfig;

        public IsolatedConfigProvider(string root)
        {
            _globalConfig = Path.Combine(root, "global.gitconfig");
            File.WriteAllText(_globalConfig, "[user]\n\tname = Test\n\temail = test@example.com\n[init]\n\tdefaultBranch = main\n");
        }

        public Task<GitEnvironment> BuildAsync(GitEnvironmentRequest request, CancellationToken ct) =>
            Task.FromResult(new GitEnvironment()
                .Set("GIT_CONFIG_GLOBAL", _globalConfig)
                .Set("GIT_CONFIG_NOSYSTEM", "1"));
    }

    private string Bare(string name)
    {
        var path = Path.Combine(_root, name + ".git");
        Directory.CreateDirectory(path);
        Run(path, "init", "--bare", "-b", "main");
        return path;
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Run(path, "init", "-b", "main");
        _git.SetIdentity(path, "Test", "test@example.com");
        Run(path, "config", "commit.gpgsign", "false");
        return path;
    }

    private void Commit(string repo, string file, string content, string message)
    {
        var full = Path.Combine(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        _git.Stage(repo, file.Replace('\\', '/'));
        _git.Commit(repo, message, "Test", "test@example.com");
    }

    private void Run(string dir, params string[] args)
    {
        var env = new GitEnvironment().Set("GIT_CONFIG_NOSYSTEM", "1").Set("GIT_CONFIG_GLOBAL", Path.Combine(_root, "global.gitconfig"));
        var r = _git.Runner.RunAsync(dir, args, new GitRunOptions { Environment = env }).GetAwaiter().GetResult();
        r.Success.Should().BeTrue($"git {string.Join(' ', args)} failed: {r.StdErr}");
    }

    // ----- discovery ---------------------------------------------------------

    [Fact]
    public void Locate_FindsEnclosingRepo_FromCollectionSubfolder()
    {
        if (!_available) return;
        var ws = Repo("workspace");
        var collection = Path.Combine(ws, "collections", "orders");
        Directory.CreateDirectory(collection);

        var ctx = GitRepoLocator.Locate(collection);

        ctx.Should().NotBeNull();
        ctx!.RepoRoot.Should().Be(Path.GetFullPath(ws).TrimEnd('\\', '/'));
        ctx.CollectionPrefix.Should().Be("collections/orders");
        _git.IsRepository(collection).Should().BeTrue("a collection inside a cloned workspace is under version control");
        _git.IsRepositoryRoot(collection).Should().BeFalse();
    }

    [Fact]
    public void Locate_ReturnsNull_OutsideAnyRepo()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        GitRepoLocator.Locate(plain).Should().BeNull();
    }

    [Theory]
    [InlineData("collections/orders/get.bru", "collections/orders", true)]
    [InlineData("collections/orders", "collections/orders", true)]
    [InlineData("collections/ordersX/get.bru", "collections/orders", false)]
    [InlineData("environments/dev.env.json", "collections/orders", false)]
    [InlineData("anything.txt", "", true)]
    public void IsUnderPrefix_MatchesWholeSegments(string path, string prefix, bool expected) =>
        GitRepoLocator.IsUnderPrefix(path, prefix).Should().Be(expected);

    // ----- remote resolution -------------------------------------------------

    [Fact]
    public void ResolveRemote_NoRemotes_ReturnsNull()
    {
        if (!_available) return;
        var repo = Repo("r");
        _git.ResolveRemote(repo, "main", forPush: true).Should().BeNull();
    }

    [Fact]
    public void ResolveRemote_SingleNonOriginRemote_IsUsed()
    {
        if (!_available) return;
        var repo = Repo("r");
        _git.AddRemote(repo, "upstream", Bare("u"));
        _git.ResolveRemote(repo, "main", forPush: true).Should().Be("upstream");
    }

    [Fact]
    public void ResolveRemote_MultipleRemotes_PrefersOrigin_ElseNeedsChoice()
    {
        if (!_available) return;
        var repo = Repo("r");
        _git.AddRemote(repo, "fork", Bare("f"));
        _git.AddRemote(repo, "company", Bare("c"));
        _git.ResolveRemote(repo, "main", forPush: true).Should().BeNull();
        _git.NeedsRemoteChoice(repo, "main").Should().BeTrue();

        _git.AddRemote(repo, "origin", Bare("o"));
        _git.ResolveRemote(repo, "main", forPush: true).Should().Be("origin");
    }

    [Fact]
    public void ResolveRemote_HonorsPushRemoteAndPushDefault()
    {
        if (!_available) return;
        var repo = Repo("r");
        _git.AddRemote(repo, "origin", Bare("o"));
        _git.AddRemote(repo, "fork", Bare("f"));
        Run(repo, "config", "remote.pushDefault", "fork");
        _git.ResolveRemote(repo, "main", forPush: true).Should().Be("fork");
        _git.ResolveRemote(repo, "main", forPush: false).Should().Be("origin");

        Run(repo, "config", "branch.main.pushRemote", "origin");
        _git.ResolveRemote(repo, "main", forPush: true).Should().Be("origin");
    }

    // ----- push / pull -------------------------------------------------------

    [Fact]
    public async Task Push_WithoutAnyRemote_ThrowsTypedNoRemote()
    {
        if (!_available) return;
        var repo = Repo("r");
        Commit(repo, "a.txt", "1", "first");

        var act = () => _git.PushAsync(repo);

        (await act.Should().ThrowAsync<GitOperationException>()).Which.Kind.Should().Be(GitErrorKind.NoRemoteConfigured);
    }

    [Fact]
    public async Task Push_ToNonOriginRemote_SetsUpstream()
    {
        if (!_available) return;
        var bare = Bare("server");
        var repo = Repo("r");
        Commit(repo, "a.txt", "1", "first");
        _git.AddRemote(repo, "company", bare);

        await _git.PushAsync(repo);

        _git.GetUpstream(repo, "main").Should().Be(("company", "main"));
        _git.AheadBehind(repo, "main").Should().Be((0, 0));
    }

    /// <summary>The reported bug: workspace cloned from the CLI, collection in a subfolder.
    /// Status / commit / push all operate on the discovered repo root.</summary>
    [Fact]
    public async Task CollectionInsideClonedWorkspace_CanCommitAndPush()
    {
        if (!_available) return;
        var bare = Bare("server");
        var seed = Repo("seed");
        Commit(seed, "workspace.yml", "name: ws", "seed");
        _git.AddRemote(seed, "origin", bare);
        await _git.PushAsync(seed);

        var clone = Path.Combine(_root, "clone");
        await _git.CloneAsync(bare, clone);
        Run(clone, "config", "commit.gpgsign", "false");
        var collection = Path.Combine(clone, "collections", "orders");
        Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(collection, "get.bru"), "meta { name: get }");

        var ctx = GitRepoLocator.Locate(collection)!;
        _git.Status(ctx.RepoRoot).Should().Contain(c => c.Path == "collections/orders/get.bru");
        _git.Stage(ctx.RepoRoot, "collections/orders/get.bru");
        _git.Commit(ctx.RepoRoot, "add orders", "Test", "test@example.com");
        await _git.PushAsync(ctx.RepoRoot);

        var info = await _git.LsRemoteAsync(bare);
        info.DefaultBranch.Should().Be("main");
        info.Branches.Should().Contain("main");
        _git.AheadBehind(ctx.RepoRoot, "main").Should().Be((0, 0));
    }

    [Fact]
    public async Task Pull_Rebase_IntegratesDivergedHistory()
    {
        if (!_available) return;
        var (a, b) = await TwoClonesAsync();
        Commit(a, "a.txt", "from a", "a change");
        await _git.PushAsync(a);
        Commit(b, "b.txt", "from b", "b change");

        await _git.PullAsync(b, GitPullMode.Rebase);

        File.Exists(Path.Combine(b, "a.txt")).Should().BeTrue();
        _git.AheadBehind(b, "main").Should().Be((1, 0));
        await _git.PushAsync(b);
    }

    [Fact]
    public async Task Pull_FastForwardOnly_OnDivergedHistory_IsClassified()
    {
        if (!_available) return;
        var (a, b) = await TwoClonesAsync();
        Commit(a, "a.txt", "from a", "a change");
        await _git.PushAsync(a);
        Commit(b, "b.txt", "from b", "b change");

        var act = () => _git.PullAsync(b, GitPullMode.FastForwardOnly);

        (await act.Should().ThrowAsync<GitOperationException>()).Which.Kind.Should().Be(GitErrorKind.NonFastForward);
    }

    [Fact]
    public async Task Push_Rejected_WhenRemoteIsAhead_IsClassifiedNonFastForward()
    {
        if (!_available) return;
        var (a, b) = await TwoClonesAsync();
        Commit(a, "a.txt", "from a", "a change");
        await _git.PushAsync(a);
        Commit(b, "b.txt", "from b", "b change");

        var act = () => _git.PushAsync(b);

        var ex = (await act.Should().ThrowAsync<GitOperationException>()).Which;
        ex.Kind.Should().Be(GitErrorKind.NonFastForward);
        ex.Error.Actions.Should().Contain(GitErrorAction.PullFirst);
    }

    [Fact]
    public async Task RebaseConflict_IsReported_AndCanBeAborted()
    {
        if (!_available) return;
        var (a, b) = await TwoClonesAsync();
        Commit(a, "same.txt", "A", "a edit");
        await _git.PushAsync(a);
        Commit(b, "same.txt", "B", "b edit");

        var act = () => _git.PullAsync(b, GitPullMode.Rebase);
        (await act.Should().ThrowAsync<GitOperationException>()).Which.Kind.Should().Be(GitErrorKind.MergeConflict);
        _git.CurrentOperation(b).Should().Be(GitRepoOperation.Rebase);

        await _git.ControlOperationAsync(b, GitOperationControl.Abort);

        _git.CurrentOperation(b).Should().Be(GitRepoOperation.None);
        File.ReadAllText(Path.Combine(b, "same.txt")).Should().Be("B");
    }

    [Fact]
    public async Task Clone_KeepsGitFolder_AndCleansUpOnFailure()
    {
        if (!_available) return;
        var bare = Bare("server");
        var seed = Repo("seed");
        Commit(seed, "x.txt", "x", "seed");
        _git.AddRemote(seed, "origin", bare);
        await _git.PushAsync(seed);

        var target = Path.Combine(_root, "kept");
        await _git.CloneAsync(bare, target);
        Directory.Exists(Path.Combine(target, ".git")).Should().BeTrue();

        var bad = Path.Combine(_root, "bad");
        var act = () => _git.CloneAsync(Path.Combine(_root, "does-not-exist.git"), bad);
        await act.Should().ThrowAsync<GitOperationException>();
        Directory.Exists(bad).Should().BeFalse("a failed clone must not leave a half-populated folder behind");
    }

    /// <summary>A repo sparse-cloned outside Vegha enables extensions.worktreeConfig, which
    /// libgit2 can't open. It must be reported as unsupported — never as "not a repository",
    /// which would offer to publish / re-init it.</summary>
    [Fact]
    public async Task SparseCheckoutRepo_IsReportedUnsupported_NotAsMissing()
    {
        if (!_available) return;
        var bare = Bare("server");
        var seed = Repo("seed");
        Commit(seed, "Postman/a.postman_collection.json", "{}", "postman");
        Commit(seed, "src/app.cs", "class A {}", "code");
        _git.AddRemote(seed, "origin", bare);
        await _git.PushAsync(seed);

        var target = Path.Combine(_root, "sparse");
        await _git.CloneAsync(bare, target);
        Run(target, "sparse-checkout", "set", "Postman");

        var ctx = GitRepoLocator.Locate(Path.Combine(target, "Postman"));

        // libgit2 would report the hidden src/app.cs as deleted — so the repo must be refused.
        ctx.Should().NotBeNull();
        ctx!.IsSupported.Should().BeFalse();
        ctx.UnsupportedReason.Should().Contain("sparse checkout");
        ctx.RepoRoot.Should().Be(Path.GetFullPath(target).TrimEnd('\\', '/'));
    }

    [Fact]
    public async Task CheckoutAsync_RemoteOnlyBranch_CreatesTrackingBranch()
    {
        if (!_available) return;
        var (a, b) = await TwoClonesAsync();
        Run(a, "switch", "-c", "feature");
        Commit(a, "f.txt", "f", "feature work");
        await _git.PushAsync(a);
        await _git.FetchAsync(b);

        await _git.CheckoutAsync(b, "feature");

        _git.CurrentBranch(b).Should().Be("feature");
        _git.GetUpstream(b, "feature").Should().Be(("origin", "feature"));
        File.Exists(Path.Combine(b, "f.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task InitAsync_UsesRequestedBranch()
    {
        if (!_available) return;
        var dir = Path.Combine(_root, "fresh");
        await _git.InitAsync(dir, "trunk");
        _git.CurrentBranch(dir).Should().Be("trunk");
        _git.IsUnborn(dir).Should().BeTrue();
    }

    private async Task<(string A, string B)> TwoClonesAsync()
    {
        var bare = Bare("server");
        var seed = Repo("seed");
        Commit(seed, "readme.txt", "seed", "seed");
        _git.AddRemote(seed, "origin", bare);
        await _git.PushAsync(seed);

        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        await _git.CloneAsync(bare, a);
        await _git.CloneAsync(bare, b);
        foreach (var r in new[] { a, b })
        {
            _git.SetIdentity(r, "Test", "test@example.com");
            Run(r, "config", "commit.gpgsign", "false");
        }
        return (a, b);
    }
}
