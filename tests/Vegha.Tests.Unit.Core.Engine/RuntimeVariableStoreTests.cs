using FluentAssertions;
using Vegha.Core.Engine;
using Xunit;

namespace Vegha.Tests.Unit.Core.Engine;

public class RuntimeVariableStoreTests
{
    [Fact]
    public void SetMany_records_provenance_and_raises_one_change()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new RuntimeVariableStore(() => now);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.SetMany(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, "Login", n => "res.body." + n);

        changes.Should().Be(1);
        store.TryGet("a", out var entry).Should().BeTrue();
        entry.SourceRequest.Should().Be("Login");
        store.Describe(entry).Should().Be("captured from Login · res.body.a · just now");
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_do_not_corrupt_the_store()
    {
        var store = new RuntimeVariableStore();
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                store.Set($"w{w}-{i}", i.ToString());
                _ = store.Snapshot();
                if (i % 50 == 0) store.Remove($"w{w}-{i}");
            }
        }));

        await Task.WhenAll(writers);

        store.Count.Should().Be(8 * (500 - 10));
    }

    [Fact]
    public void Clear_on_empty_store_raises_nothing()
    {
        var store = new RuntimeVariableStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Clear();
        changes.Should().Be(0);
    }
}
