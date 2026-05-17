using Vegha.Integrations.Git;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

/// <summary>Covers the debounced repo-change event. FileSystemWatcher is timing-sensitive on
/// Windows so the asserts allow a generous wait window.</summary>
public class GitRepoWatcherTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "vegha-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();

    public GitRepoWatcherTests()
    {
        Directory.CreateDirectory(_repo);
        _git.Init(_repo);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task WritingAFile_RaisesDebouncedEvent()
    {
        using var watcher = new GitRepoWatcher(_repo) { DebounceMs = 50 };
        var tcs = new TaskCompletionSource();
        watcher.RepositoryChanged += (_, _) => tcs.TrySetResult();

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        completed.Should().Be(tcs.Task, "FileSystemWatcher should fire within 3s for a tracked write");
    }

    [Fact]
    public async Task EventsInsideGitObjects_AreIgnored()
    {
        using var watcher = new GitRepoWatcher(_repo) { DebounceMs = 50 };
        int fires = 0;
        watcher.RepositoryChanged += (_, _) => Interlocked.Increment(ref fires);

        // Touching a file under .git/objects/ — the watcher filters those out so we don't
        // re-refresh during every git operation.
        var objectsDir = Path.Combine(_repo, ".git", "objects");
        Directory.CreateDirectory(objectsDir);
        File.WriteAllText(Path.Combine(objectsDir, "noise.txt"), "x");

        await Task.Delay(500);
        fires.Should().Be(0);
    }
}
