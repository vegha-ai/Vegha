using Vegha.Integrations.Git;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

public class GitServiceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "vegha-git-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();

    public GitServiceTests()
    {
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            // git makes the .git dir read-only; force-clean.
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- existing tests preserved ----------------------------------------

    [Fact]
    public void Init_MakesDirectoryRecognizedAsRepo()
    {
        _git.IsRepository(_repo).Should().BeFalse();
        _git.Init(_repo);
        _git.IsRepository(_repo).Should().BeTrue();
    }

    [Fact]
    public void Status_ReportsUntrackedFiles()
    {
        _git.Init(_repo);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");

        var status = _git.Status(_repo);
        status.Should().ContainSingle();
        status[0].Path.Should().Be("a.txt");
        status[0].Kind.Should().Be(GitChangeKind.Added);
        status[0].IsStaged.Should().BeFalse();
    }

    [Fact]
    public void StageThenCommit_AdvancesHead_AndClearsStatus()
    {
        _git.Init(_repo);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");
        _git.Stage(_repo, "a.txt");

        var staged = _git.Status(_repo);
        staged[0].IsStaged.Should().BeTrue();

        _git.Commit(_repo, "first commit", "Test", "test@example.com");
        _git.Status(_repo).Should().BeEmpty();

        var commits = _git.RecentCommits(_repo);
        commits.Should().ContainSingle();
        commits[0].MessageShort.Should().Be("first commit");
        commits[0].AuthorName.Should().Be("Test");
    }

    [Fact]
    public void CreateAndCheckoutBranch_SwitchesHead()
    {
        InitWithSeed();
        _git.CreateBranch(_repo, "feature");
        _git.Checkout(_repo, "feature");
        _git.CurrentBranch(_repo).Should().Be("feature");
        _git.Branches(_repo).Should().Contain("feature");
    }

    [Fact]
    public void UnstagedDiff_ContainsModifiedHunk()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "first\nsecond\n");
        var diff = _git.UnstagedDiff(_repo);
        diff.Should().Contain("+second");
    }

    // ----- new tests -------------------------------------------------------

    [Fact]
    public void DiscardFile_RestoresHeadContent()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "dirty\n");
        _git.Status(_repo).Should().ContainSingle(c => c.Path == "a.txt");

        _git.DiscardFile(_repo, "a.txt");

        File.ReadAllText(Path.Combine(_repo, "a.txt")).Should().Be("first\n");
        _git.Status(_repo).Should().BeEmpty();
    }

    [Fact]
    public void DiscardAll_RevertsWorkingTreeToHead()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "dirty\n");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "tracked\n");
        _git.Stage(_repo, "b.txt");

        _git.DiscardAll(_repo);

        File.ReadAllText(Path.Combine(_repo, "a.txt")).Should().Be("first\n");
        // b.txt was staged but never committed; DiscardAll (hard reset to HEAD) leaves it as an
        // unstaged untracked file because hard reset only affects tracked content.
        var status = _git.Status(_repo);
        status.Where(s => s.Path == "b.txt" && s.IsStaged).Should().BeEmpty();
    }

    [Fact]
    public void AmendCommit_UpdatesMessage_WithoutAddingCommit()
    {
        InitWithSeed();
        var beforeCount = _git.RecentCommits(_repo).Count;

        _git.AmendCommit(_repo, message: "init (amended)", authorName: "T", authorEmail: "t@x");

        var commits = _git.RecentCommits(_repo);
        commits.Count.Should().Be(beforeCount);
        commits[0].MessageShort.Should().Be("init (amended)");
    }

    [Fact]
    public void Reset_Hard_RevertsToReferencedCommit()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "second\n");
        _git.Stage(_repo, "a.txt");
        _git.Commit(_repo, "second", "T", "t@x");
        var headSha = _git.RecentCommits(_repo)[0].Sha;
        var prevSha = _git.RecentCommits(_repo)[1].Sha;

        _git.Reset(_repo, ResetMode.Hard, prevSha);

        File.ReadAllText(Path.Combine(_repo, "a.txt")).Should().Be("first\n");
        _git.RecentCommits(_repo)[0].Sha.Should().Be(prevSha);
        _git.RecentCommits(_repo)[0].Sha.Should().NotBe(headSha);
    }

    [Fact]
    public void CreateBranchFrom_DeleteBranch_RenameBranch()
    {
        InitWithSeed();
        var headSha = _git.RecentCommits(_repo)[0].Sha;

        _git.CreateBranchFrom(_repo, "feature", headSha);
        _git.Branches(_repo).Should().Contain("feature");

        _git.RenameBranch(_repo, "feature", "feature2");
        _git.Branches(_repo).Should().Contain("feature2").And.NotContain("feature");

        _git.DeleteBranch(_repo, "feature2");
        _git.Branches(_repo).Should().NotContain("feature2");
    }

    [Fact]
    public void Stash_Pop_RestoresDirtyWorkingTree()
    {
        InitWithSeed();
        _git.SetIdentity(_repo, "Stasher", "s@x");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "dirty\n");

        _git.Stash(_repo, "wip");

        // Working tree clean after stash.
        File.ReadAllText(Path.Combine(_repo, "a.txt")).Should().Be("first\n");
        _git.StashList(_repo).Should().ContainSingle();

        _git.StashPop(_repo);
        File.ReadAllText(Path.Combine(_repo, "a.txt")).Should().Be("dirty\n");
        _git.StashList(_repo).Should().BeEmpty();
    }

    [Fact]
    public void GetIdentity_ReadsFromConfig()
    {
        _git.Init(_repo);
        _git.SetIdentity(_repo, "Configured", "c@x");

        var (name, email) = _git.GetIdentity(_repo);
        name.Should().Be("Configured");
        email.Should().Be("c@x");

        _git.GetSignature(_repo).Should().NotBeNull();
        _git.GetSignature(_repo)!.Name.Should().Be("Configured");
    }

    [Fact]
    public void FileDiffs_HeadToWorkingTree_ParsesHunks()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "first\nsecond\nthird\n");

        var diffs = _git.FileDiffs(_repo, DiffSource.Head, DiffSource.WorkingTree, "a.txt");

        diffs.Should().ContainSingle();
        diffs[0].Path.Should().Be("a.txt");
        diffs[0].IsBinary.Should().BeFalse();
        diffs[0].Hunks.Should().NotBeEmpty();
        diffs[0].Hunks[0].Lines.Should().Contain(l => l.Kind == DiffLineKind.Added && l.Text == "second");
    }

    [Fact]
    public void GetFileAt_Head_ReturnsCommittedContent()
    {
        InitWithSeed();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "dirty\n");

        var headContent = _git.GetFileAt(_repo, "HEAD", "a.txt");
        var workdirContent = _git.GetFileAt(_repo, "WORKDIR", "a.txt");

        headContent.Should().Be("first\n");
        workdirContent.Should().Be("dirty\n");
    }

    [Fact]
    public void AheadBehind_ReportsZeroWhenNoUpstream()
    {
        InitWithSeed();
        // No upstream configured yet — should report (0, 0) rather than throw.
        var (ahead, behind) = _git.AheadBehind(_repo, "main");
        ahead.Should().Be(0);
        behind.Should().Be(0);
        _git.GetUpstream(_repo, "main").Should().BeNull();
    }

    [Fact]
    public void RemoteBranches_EmptyByDefault()
    {
        InitWithSeed();
        _git.RemoteBranches(_repo).Should().BeEmpty();
    }

    [Fact]
    public void AddRemote_AppearsInRemotesList_AndCanBeRemoved()
    {
        InitWithSeed();
        _git.Remotes(_repo).Should().BeEmpty();

        _git.AddRemote(_repo, "origin", "https://example.com/repo.git");
        var remotes = _git.Remotes(_repo);
        remotes.Should().ContainSingle();
        remotes[0].Name.Should().Be("origin");
        remotes[0].Url.Should().Be("https://example.com/repo.git");

        _git.UpdateRemoteUrl(_repo, "origin", "https://example.com/other.git");
        _git.Remotes(_repo)[0].Url.Should().Be("https://example.com/other.git");

        _git.RemoveRemote(_repo, "origin");
        _git.Remotes(_repo).Should().BeEmpty();
    }

    // ----- helpers ---------------------------------------------------------

    /// <summary>Init a repo with a single committed file <c>a.txt</c> containing "first\n",
    /// and ensure user.name / user.email are set so subsequent commits work without args.
    /// Disables <c>core.autocrlf</c> so line-ending behavior is the same on every platform.</summary>
    private void InitWithSeed()
    {
        _git.Init(_repo);
        using (var repo = new LibGit2Sharp.Repository(_repo))
            repo.Config.Set("core.autocrlf", false, LibGit2Sharp.ConfigurationLevel.Local);
        _git.SetIdentity(_repo, "T", "t@x");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "first\n");
        _git.Stage(_repo, "a.txt");
        _git.Commit(_repo, "init", "T", "t@x");
    }
}
