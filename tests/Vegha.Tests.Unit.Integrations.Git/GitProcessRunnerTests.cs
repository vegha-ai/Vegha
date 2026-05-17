using Vegha.Integrations.Git;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

/// <summary>Covers <see cref="GitProcessRunner"/> in isolation. The runner is "best-effort":
/// when git.exe isn't on PATH the probe sets <c>IsAvailable=false</c> and these tests
/// short-circuit silently rather than fail — CI environments without git installed shouldn't
/// see spurious failures.</summary>
public class GitProcessRunnerTests
{
    private readonly GitProcessRunner _runner = new();

    [Fact]
    public void Version_ProbeReturnsString_WhenGitOnPath()
    {
        if (!_runner.IsAvailable) return;
        _runner.Version.Should().NotBeNull();
        _runner.Version!.Should().StartWith("git version");
    }

    [Fact]
    public async Task RunAsync_ReturnsZeroExit_OnSuccess()
    {
        if (!_runner.IsAvailable) return;
        var dir = Path.GetTempPath();
        var result = await _runner.RunAsync(dir, new[] { "--version" });
        result.ExitCode.Should().Be(0);
        result.Success.Should().BeTrue();
        result.StdOut.Should().Contain("git version");
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExit_OnInvalidSubcommand()
    {
        if (!_runner.IsAvailable) return;
        var dir = Path.GetTempPath();
        var result = await _runner.RunAsync(dir, new[] { "this-is-not-a-git-subcommand" });
        result.ExitCode.Should().NotBe(0);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_QuotesArgsWithSpacesCorrectly()
    {
        if (!_runner.IsAvailable) return;
        var dir = Path.GetTempPath();
        // -c key=val survives ArgumentList quoting; an arg containing a space verifies the
        // process-runner doesn't split it.
        var result = await _runner.RunAsync(dir, new[] { "-c", "user.name=value with spaces", "--version" });
        result.ExitCode.Should().Be(0);
    }
}
