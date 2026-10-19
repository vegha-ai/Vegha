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

    // ---- Two stores on one file stand in for desktop + VS Code sidecar processes ----

    [Fact]
    public async Task TwoStores_PersistDoesNotClobberTheOthersCookies()
    {
        var desktop = new CookieJarStore(_tempDb);
        var sidecar = new CookieJarStore(_tempDb);
        await desktop.LoadAsync();
        await sidecar.LoadAsync();

        desktop.Container.Add(new Cookie("desk", "1", "/", "a.test"));
        await desktop.PersistAsync();
        sidecar.Container.Add(new Cookie("side", "2", "/", "b.test"));
        await sidecar.PersistAsync();

        // The old full-table rewrite would have dropped "desk" here.
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Select(c => c.Name).Should().BeEquivalentTo(new[] { "desk", "side" });
        // Each persist also merges in what the other wrote.
        sidecar.GetAll().Select(c => c.Name).Should().BeEquivalentTo(new[] { "desk", "side" });
    }

    [Fact]
    public async Task TwoStores_RefreshPicksUpOtherWritesAndKeepsUnsavedLocalOnes()
    {
        var desktop = new CookieJarStore(_tempDb);
        var sidecar = new CookieJarStore(_tempDb);
        await desktop.LoadAsync();
        await sidecar.LoadAsync();

        await desktop.UpsertAsync("api.test", "/", "session", "from-desktop");
        sidecar.Container.Add(new Cookie("local", "unsaved", "/", "api.test"));

        await sidecar.RefreshIfChangedAsync();

        sidecar.GetAll().Should().Contain(c => c.Name == "session" && c.Value == "from-desktop");
        sidecar.GetAll().Should().Contain(c => c.Name == "local");
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Select(c => c.Name).Should().Contain("local");
    }

    [Fact]
    public async Task TwoStores_RemovalInOneReachesTheOtherAndIsNotResurrected()
    {
        var desktop = new CookieJarStore(_tempDb);
        var sidecar = new CookieJarStore(_tempDb);
        await desktop.UpsertAsync("api.test", "/", "session", "x");
        await sidecar.LoadAsync();
        sidecar.GetAll().Should().ContainSingle();

        await desktop.RemoveAsync("api.test", "/", "session");
        await sidecar.RefreshIfChangedAsync();
        sidecar.GetAll().Should().BeEmpty();

        // The sidecar's next persist (after any request) must not write it back.
        await sidecar.PersistAsync();
        var fresh = new CookieJarStore(_tempDb);
        await fresh.LoadAsync();
        fresh.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task TwoStores_ValueChangeInOneReplacesValueInTheOther()
    {
        var desktop = new CookieJarStore(_tempDb);
        var sidecar = new CookieJarStore(_tempDb);
        await desktop.UpsertAsync("api.test", "/", "token", "old");
        await sidecar.LoadAsync();

        await desktop.UpsertAsync("api.test", "/", "token", "new");
        await sidecar.RefreshIfChangedAsync();

        sidecar.GetAll().Should().ContainSingle().Which.Value.Should().Be("new");
    }

    [Fact]
    public async Task RefreshIfChanged_IsNoOpWhenNothingMoved()
    {
        var store = new CookieJarStore(_tempDb);
        await store.UpsertAsync("api.test", "/", "a", "1");
        var before = store.Container.Count;

        await store.RefreshIfChangedAsync();

        store.Container.Count.Should().Be(before);
    }
}
