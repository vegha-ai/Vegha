using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class OpenApiLinkStoreTests : IDisposable
{
    private readonly string _root;

    public OpenApiLinkStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-openapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FilePath_IsUnderDotVegha()
    {
        var store = new OpenApiLinkStore();
        var path = store.FilePathForCollection(_root);
        path.Should().Be(Path.Combine(_root, ".vegha", "openapi-links.json"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new OpenApiLinkStore();
        var ts = DateTimeOffset.UtcNow;
        store.Save(_root, new List<OpenApiLink>
        {
            new("petstore", "https://example.test/petstore.yaml", ts),
            new("internal", "/local/path/spec.json"),
        });

        var loaded = store.Load(_root);
        loaded.Should().HaveCount(2);
        loaded[0].Source.Should().Be("https://example.test/petstore.yaml");
        loaded[0].LastSyncedAt.Should().BeCloseTo(ts, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Add_MergesByExistingSource()
    {
        var store = new OpenApiLinkStore();
        store.Add(_root, new OpenApiLink("petstore-v1", "https://x.test/spec.yaml"));
        store.Add(_root, new OpenApiLink("petstore-v2", "https://x.test/spec.yaml"));  // same source

        var loaded = store.Load(_root);
        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("petstore-v2");
    }

    [Fact]
    public void Remove_DropsLinkBySource()
    {
        var store = new OpenApiLinkStore();
        store.Save(_root, new List<OpenApiLink>
        {
            new("a", "https://a.test/"),
            new("b", "https://b.test/"),
        });
        store.Remove(_root, "https://a.test/");
        store.Load(_root).Should().ContainSingle().Which.Name.Should().Be("b");
    }

    [Fact]
    public void TouchSyncedAt_UpdatesTimestamp_WithoutTouchingOthers()
    {
        var store = new OpenApiLinkStore();
        store.Save(_root, new List<OpenApiLink>
        {
            new("a", "https://a.test/"),
            new("b", "https://b.test/"),
        });

        var when = DateTimeOffset.UtcNow;
        store.TouchSyncedAt(_root, "https://a.test/", when);

        var loaded = store.Load(_root);
        loaded.Single(l => l.Name == "a").LastSyncedAt.Should().BeCloseTo(when, TimeSpan.FromMilliseconds(1));
        loaded.Single(l => l.Name == "b").LastSyncedAt.Should().BeNull();
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var store = new OpenApiLinkStore();
        store.Load(_root).Should().BeEmpty();
    }
}
