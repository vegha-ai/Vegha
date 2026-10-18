using FluentAssertions;
using Vegha.Core.Persistence;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

/// <summary>The desktop app, CLI and VS Code sidecar share <c>%LocalAppData%/Vegha</c>. These
/// tests stand in for separate processes with independent store instances on the same files.</summary>
public class MultiProcessSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vegha-mp-{Guid.NewGuid():N}");

    public MultiProcessSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ConcurrentKeyCreation_AllWritersShareOneKey()
    {
        // Fresh machine: every store races to create env-secrets.key. Each saves under its own
        // env id; afterwards a fresh store must decrypt every one, which only holds if all
        // writers ended up encrypting with the same key.
        var keyDir = Path.Combine(_dir, "key");
        var collection = Path.Combine(_dir, "collection");
        Directory.CreateDirectory(collection);
        const int writers = 16;

        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            var store = new EnvironmentSecretStore(keyDir);
            start.Wait();
            store.Save(collection, $"env{i}", new Dictionary<string, string> { ["token"] = $"secret-{i}" });
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        var reader = new EnvironmentSecretStore(keyDir);
        for (var i = 0; i < writers; i++)
            reader.Load(collection, $"env{i}").Should().ContainKey("token").WhoseValue.Should().Be($"secret-{i}");
        Directory.GetFiles(keyDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task AtomicFile_ConcurrentReaderNeverSeesPartialContent()
    {
        var path = Path.Combine(_dir, "state.json");
        string Payload(int n) => "{\"n\":" + n + ",\"pad\":\"" + new string('x', 64 * 1024) + "\"}";
        AtomicFile.WriteAllText(path, Payload(0));

        using var cts = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (!text.EndsWith("\"}", StringComparison.Ordinal)) Interlocked.Increment(ref torn);
                }
                catch (IOException) { /* replaced mid-open — fine, never partial */ }
                catch (UnauthorizedAccessException) { /* Windows pending-delete window */ }
                // A real reader (the other process loading settings) isn't a hot loop; the
                // gap is also what lets the writer's rename through on Windows.
                Thread.Sleep(1);
            }
        });

        for (var i = 1; i <= 100; i++) AtomicFile.WriteAllText(path, Payload(i));
        cts.Cancel();
        await reader;

        torn.Should().Be(0);
        File.ReadAllText(path).Should().StartWith("{\"n\":100,");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void AtomicFile_CreatesMissingDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "file.bin");
        AtomicFile.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }
}
