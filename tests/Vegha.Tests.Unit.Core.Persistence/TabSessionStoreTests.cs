using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class TabSessionStoreTests
{
    [Fact]
    public void EmptyOnFirstUse_LoadReturnsEmpty()
    {
        var workspaceId = "tabsession-empty-" + Guid.NewGuid().ToString("N");
        var store = new TabSessionStore(workspaceId);
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_Entries()
    {
        var workspaceId = "tabsession-rt-" + Guid.NewGuid().ToString("N");
        var store = new TabSessionStore(workspaceId);
        try
        {
            var entries = new[]
            {
                new TabSessionEntry("a.req.json", "/path/a.req.json", "a", "Http", IsActive: false),
                new TabSessionEntry("b.req.json", "/path/b.req.json", "b", "Http", IsActive: true),
                new TabSessionEntry("draft:1", null, "draft", "Http", IsActive: false),
            };
            store.Save(entries);

            var loaded = store.Load();
            loaded.Should().HaveCount(3);
            loaded[0].Id.Should().Be("a.req.json");
            loaded[1].IsActive.Should().BeTrue();
            loaded[2].SourcePath.Should().BeNull();
        }
        finally
        {
            store.Save(Array.Empty<TabSessionEntry>());
        }
    }

    [Fact]
    public void WorkspaceIdsAreScoped()
    {
        var idA = "tabsession-A-" + Guid.NewGuid().ToString("N");
        var idB = "tabsession-B-" + Guid.NewGuid().ToString("N");
        var a = new TabSessionStore(idA);
        var b = new TabSessionStore(idB);
        try
        {
            a.Save(new[] { new TabSessionEntry("x", "/x", "x", "Http", false) });
            b.Save(Array.Empty<TabSessionEntry>());

            a.Load().Should().HaveCount(1);
            b.Load().Should().BeEmpty();
        }
        finally
        {
            a.Save(Array.Empty<TabSessionEntry>());
        }
    }

    [Fact]
    public void InvalidJsonFile_LoadFallsBackToEmpty()
    {
        var workspaceId = "tabsession-bad-" + Guid.NewGuid().ToString("N");
        // Pre-populate the file with junk so Load() takes the catch path.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"tabs-{workspaceId}.json");
        File.WriteAllText(file, "not json");

        try
        {
            var store = new TabSessionStore(workspaceId);
            store.Load().Should().BeEmpty();
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}
