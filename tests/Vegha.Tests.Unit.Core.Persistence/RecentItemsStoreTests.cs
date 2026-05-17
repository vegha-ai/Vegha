using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class RecentItemsStoreTests : IDisposable
{
    private readonly string _dir;

    public RecentItemsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vegha-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Touch_ThenLoad_PrependsNewEntry()
    {
        var store = new RecentItemsStore(_dir);
        store.Touch("/path/a", DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        store.Touch("/path/b", DateTimeOffset.Parse("2025-01-02T00:00:00Z"));

        var items = store.Load();
        items[0].Path.Should().Be("/path/b");
        items[1].Path.Should().Be("/path/a");
    }

    [Fact]
    public void Touch_ExistingPath_MovesToFront_NoDuplicates()
    {
        var store = new RecentItemsStore(_dir);
        store.Touch("/x");
        store.Touch("/y");
        store.Touch("/x");  // re-touch — should bubble to front

        var items = store.Load();
        items.Should().HaveCount(2);
        items[0].Path.Should().Be("/x");
        items[1].Path.Should().Be("/y");
    }

    [Fact]
    public void Touch_PastMaxItems_EvictsOldest()
    {
        var store = new RecentItemsStore(_dir);
        for (var i = 0; i < RecentItemsStore.MaxItems + 5; i++)
            store.Touch($"/p/{i}", DateTimeOffset.UtcNow.AddSeconds(i));
        store.Load().Count.Should().Be(RecentItemsStore.MaxItems);
    }

    [Fact]
    public void Remove_DropsItem()
    {
        var store = new RecentItemsStore(_dir);
        store.Touch("/a");
        store.Touch("/b");
        store.Remove("/a");
        store.Load().Should().ContainSingle().Which.Path.Should().Be("/b");
    }

    [Fact]
    public void Clear_LeavesNoFile()
    {
        var store = new RecentItemsStore(_dir);
        store.Touch("/a");
        store.Clear();
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        new RecentItemsStore(_dir).Load().Should().BeEmpty();
    }
}
