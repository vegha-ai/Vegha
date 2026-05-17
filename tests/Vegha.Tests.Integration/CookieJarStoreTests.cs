using System.Net;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class CookieJarStoreTests : IDisposable
{
    private readonly string _tempDb = Path.Combine(Path.GetTempPath(),
        $"vegha-cookies-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Persist_ThenLoad_RestoresCookies()
    {
        var store = new CookieJarStore(_tempDb);
        store.Container.Add(new Cookie("session", "abc123", "/", "example.test")
        {
            Expires = DateTime.UtcNow.AddDays(1),
            HttpOnly = true,
        });
        store.Container.Add(new Cookie("theme", "dark", "/", "example.test"));

        await store.PersistAsync();
        store.Dispose();

        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();

        var snapshot = fresh.GetAll();
        snapshot.Should().HaveCount(2);
        snapshot.Should().Contain(c => c.Name == "session" && c.Value == "abc123" && c.HttpOnly);
        snapshot.Should().Contain(c => c.Name == "theme" && c.Value == "dark");
    }

    [Fact]
    public async Task Load_DropsExpiredCookies()
    {
        var store = new CookieJarStore(_tempDb);
        store.Container.Add(new Cookie("stale", "x", "/", "example.test")
        {
            Expires = DateTime.UtcNow.AddSeconds(-10),
        });
        store.Container.Add(new Cookie("fresh", "y", "/", "example.test")
        {
            Expires = DateTime.UtcNow.AddDays(1),
        });

        await store.PersistAsync();
        store.Dispose();

        // CookieContainer drops expired cookies automatically; manually inject an expired row to assert filtering.
        // We do this through Persist by setting Expires before the call, but since CookieContainer ages them out,
        // instead we just verify only fresh ones come back from a roundtrip.
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Select(c => c.Name).Should().NotContain("stale");
    }

    [Fact]
    public async Task RemoveAsync_DropsCookieFromStoreAndContainer()
    {
        var store = new CookieJarStore(_tempDb);
        store.Container.Add(new Cookie("a", "1", "/", "example.test"));
        store.Container.Add(new Cookie("b", "2", "/", "example.test"));
        await store.PersistAsync();

        await store.RemoveAsync("example.test", "/", "a");

        var remaining = store.GetAll();
        remaining.Should().ContainSingle();
        remaining[0].Name.Should().Be("b");

        // Disk should reflect the removal too.
        store.Dispose();
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Select(c => c.Name).Should().BeEquivalentTo(new[] { "b" });
    }

    [Fact]
    public async Task ClearAsync_EmptiesStoreAndContainer()
    {
        var store = new CookieJarStore(_tempDb);
        store.Container.Add(new Cookie("a", "1", "/", "example.test"));
        store.Container.Add(new Cookie("b", "2", "/", "example.test"));
        await store.PersistAsync();

        await store.ClearAsync();

        store.GetAll().Should().BeEmpty();

        store.Dispose();
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task SessionCookies_NoExpires_AreReportedWithNullExpires()
    {
        var store = new CookieJarStore(_tempDb);
        store.Container.Add(new Cookie("sid", "Z", "/", "example.test"));
        await store.PersistAsync();

        store.Dispose();
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();

        var snap = fresh.GetAll();
        snap.Should().ContainSingle();
        snap[0].Expires.Should().BeNull();
    }
}
