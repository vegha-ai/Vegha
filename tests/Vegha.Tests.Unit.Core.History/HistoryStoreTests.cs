using Vegha.Core.History;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.History;

public class HistoryStoreTests : IDisposable
{
    private readonly string _tempDb;

    public HistoryStoreTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"Vegha-history-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // SQLite keeps a connection pool warm; clear it so the test can delete the file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDb))
        {
            try { File.Delete(_tempDb); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private HistoryStore Store() => new(_tempDb);

    [Fact]
    public async Task RequestBlob_PersistsAndReads_BackForReplay()
    {
        var store = Store();
        var blob = """{"method":"POST","url":"https://x/y","headers":[]}""";
        var id = await store.AppendAsync("POST", "https://x/y", 201, 12, "{}", null, default, blob);
        var got = await store.GetRequestBlobAsync(id);
        got.Should().Be(blob);
    }

    [Fact]
    public async Task RequestBlob_NullByDefault_ForLegacyAppendCall()
    {
        var store = Store();
        var id = await store.AppendAsync("GET", "https://x/y", 200, 1, "ok", null);
        (await store.GetRequestBlobAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_StillPersistsRequestBlob_SoReplayWorks()
    {
        // The compliance toggle suppresses response payloads only. The request snapshot is the
        // user's own request and must always be stored, otherwise History → Replay degrades to
        // URL-only (the regression this test pins).
        var store = Store();
        store.Enabled = false;
        var blob = """{"Method":"POST","Url":"https://x/y","Body":{"Type":"json"}}""";
        var id = await store.AppendAsync("POST", "https://x/y", 201, 12, "secret-response", null, default, blob);

        (await store.GetRequestBlobAsync(id)).Should().Be(blob);
    }

    [Fact]
    public async Task Disabled_DropsResponsePreview_ButKeepsRow()
    {
        var store = Store();
        store.Enabled = false;
        await store.AppendAsync("GET", "https://x/y", 200, 5, "secret-response", null);

        var rows = await store.GetRecentAsync();
        rows.Should().ContainSingle();
        rows[0].Url.Should().Be("https://x/y");
        rows[0].ResponseBodyPreview.Should().BeNull();
    }

    [Fact]
    public async Task Append_ThenGetRecent_RoundTrips()
    {
        var store = Store();
        var id = await store.AppendAsync("GET", "https://x/y", 200, 137, "hello", null);
        id.Should().BeGreaterThan(0);

        var rows = await store.GetRecentAsync();
        rows.Should().ContainSingle();
        rows[0].Method.Should().Be("GET");
        rows[0].Url.Should().Be("https://x/y");
        rows[0].StatusCode.Should().Be(200);
        rows[0].DurationMs.Should().Be(137);
        rows[0].ResponseBodyPreview.Should().Be("hello");
        rows[0].ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task GetRecent_ReturnsMostRecentFirst()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://x/1", 200, 10, "a", null);
        await Task.Delay(5);
        await store.AppendAsync("GET", "https://x/2", 200, 20, "b", null);
        await Task.Delay(5);
        await store.AppendAsync("GET", "https://x/3", 200, 30, "c", null);

        var rows = await store.GetRecentAsync();
        rows.Select(r => r.Url).Should().Equal("https://x/3", "https://x/2", "https://x/1");
    }

    [Fact]
    public async Task GetRecent_LimitParameter_Honored()
    {
        var store = Store();
        for (int i = 0; i < 10; i++)
            await store.AppendAsync("GET", $"https://x/{i}", 200, i, $"b{i}", null);

        var rows = await store.GetRecentAsync(limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task LongResponseBody_IsTruncated()
    {
        var store = Store();
        var giant = new string('a', HistoryStore.DefaultPreviewMaxChars + 500);
        await store.AppendAsync("GET", "https://x", 200, 1, giant, null);

        var rows = await store.GetRecentAsync();
        rows[0].ResponseBodyPreview!.Length.Should().Be(HistoryStore.DefaultPreviewMaxChars + 1); // includes ellipsis
        rows[0].ResponseBodyPreview!.EndsWith("…").Should().BeTrue();
    }

    [Fact]
    public async Task Append_OverMaxRetained_PrunesOldest()
    {
        // Drive the count prune via a tight runtime cap so the test stays fast.
        var store = Store();
        store.MaxRetained = 10;
        var total = store.MaxRetained + 5;
        for (int i = 0; i < total; i++)
            await store.AppendAsync("GET", $"https://x/{i}", 200, i, null, null);

        var count = await store.CountAsync();
        count.Should().Be(store.MaxRetained);

        // The very first entry (i=0) should be gone; the last (i=total-1) should be present.
        var rows = await store.GetRecentAsync(limit: store.MaxRetained);
        rows.Should().Contain(r => r.Url == $"https://x/{total - 1}");
        rows.Should().NotContain(r => r.Url == "https://x/0");
    }

    [Fact]
    public async Task ErrorMessage_PersistsAndStatusCanBeZero()
    {
        var store = Store();
        await store.AppendAsync("GET", "http://127.0.0.1:1", 0, 11, null, "Connection refused");

        var rows = await store.GetRecentAsync();
        rows[0].StatusCode.Should().Be(0);
        rows[0].ErrorMessage.Should().Contain("Connection refused");
        rows[0].ResponseBodyPreview.Should().BeNull();
    }

    [Fact]
    public async Task Clear_Empties()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://x", 200, 1, "b", null);
        await store.ClearAsync();
        (await store.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_RemovesRowById()
    {
        var store = Store();
        var id1 = await store.AppendAsync("GET", "https://x/1", 200, 1, null, null);
        var id2 = await store.AppendAsync("GET", "https://x/2", 200, 1, null, null);

        await store.DeleteAsync(id1);

        var rows = await store.GetRecentAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(id2);
    }

    [Fact]
    public async Task Reopening_PersistsAcrossInstances()
    {
        var s1 = Store();
        await s1.AppendAsync("GET", "https://x", 200, 1, "b", null);
        s1.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var s2 = Store();
        var rows = await s2.GetRecentAsync();
        rows.Should().ContainSingle();
        rows[0].Url.Should().Be("https://x");
    }

    [Fact]
    public async Task Timestamp_IsRecent()
    {
        var store = Store();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await store.AppendAsync("GET", "https://x", 200, 1, null, null);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var rows = await store.GetRecentAsync();
        rows[0].TimestampUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task GetRangeAsync_OffsetLimit_ReturnsExpectedSlice()
    {
        var store = Store();
        // Insert 10 rows; the store orders by timestamp DESC so the LAST insert is row 0.
        for (int i = 0; i < 10; i++)
        {
            await store.AppendAsync("GET", $"https://x/{i}", 200, i, null, null);
            await Task.Delay(2); // make timestamp distinct
        }

        var page1 = await store.GetRangeAsync(offset: 0, limit: 3);
        page1.Should().HaveCount(3);
        page1[0].Url.Should().Be("https://x/9");
        page1[2].Url.Should().Be("https://x/7");

        var page2 = await store.GetRangeAsync(offset: 3, limit: 3);
        page2.Should().HaveCount(3);
        page2[0].Url.Should().Be("https://x/6");
        page2[2].Url.Should().Be("https://x/4");

        // Offset past the end returns an empty slice without throwing.
        var pastEnd = await store.GetRangeAsync(offset: 100, limit: 3);
        pastEnd.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_PrunesByAge_DropsOlderThanMaxAge()
    {
        var store = Store();
        // MaxAge must comfortably exceed the insert→prune gap inside a single AppendAsync:
        // the age prune computes its cutoff from UtcNow taken just after the row is inserted,
        // so a too-small MaxAge lets execution jitter on a loaded CI runner age the fresh row
        // out of its own append. One second leaves a generous margin while staying short
        // enough that the delay below still pushes the old rows past the cutoff.
        store.MaxAge = TimeSpan.FromSeconds(1);

        // Two old rows, then wait past the cutoff, then a fresh row triggers the prune.
        await store.AppendAsync("GET", "https://old/1", 200, 1, null, null);
        await store.AppendAsync("GET", "https://old/2", 200, 1, null, null);
        await Task.Delay(1200);
        await store.AppendAsync("GET", "https://fresh", 200, 1, null, null);

        var rows = await store.GetRecentAsync();
        rows.Should().ContainSingle(r => r.Url == "https://fresh");
        rows.Should().NotContain(r => r.Url == "https://old/1");
        rows.Should().NotContain(r => r.Url == "https://old/2");
    }

    [Fact]
    public async Task PruneAsync_OneShot_DropsAgedAndOverflowRows()
    {
        var store = Store();
        store.MaxAge = TimeSpan.Zero; // disable age prune for setup
        store.MaxRetained = 10_000;   // disable count prune for setup
        for (int i = 0; i < 5; i++)
            await store.AppendAsync("GET", $"https://x/{i}", 200, i, null, null);

        // Tighten the policy and prune-on-demand.
        store.MaxRetained = 3;
        store.MaxAge = TimeSpan.FromMilliseconds(1);
        await Task.Delay(20);
        await store.PruneAsync();

        var count = await store.CountAsync();
        count.Should().Be(0); // all rows are older than 1ms cutoff
    }

    [Fact]
    public async Task MaxRetained_RuntimeChange_TakesEffectOnNextInsert()
    {
        var store = Store();
        store.MaxRetained = 100;
        for (int i = 0; i < 3; i++)
            await store.AppendAsync("GET", $"https://x/{i}", 200, i, null, null);
        (await store.CountAsync()).Should().Be(3);

        store.MaxRetained = 2;
        // Inserting a new row triggers the count prune with the new cap.
        await store.AppendAsync("GET", "https://x/trigger", 200, 0, null, null);

        (await store.CountAsync()).Should().Be(2);
    }

    // ============================== per-workspace history ==============================

    [Fact]
    public async Task GetRange_ScopedToWorkspace_ReturnsOnlyThatWorkspacesRows()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://a/1", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://a/2", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://b/1", 200, 1, null, null, workspaceId: "/ws/B");

        var a = await store.GetRangeAsync(0, 100, workspaceId: "/ws/A");
        a.Select(r => r.Url).Should().BeEquivalentTo("https://a/1", "https://a/2");

        var b = await store.GetRangeAsync(0, 100, workspaceId: "/ws/B");
        b.Select(r => r.Url).Should().BeEquivalentTo("https://b/1");

        // Null workspace = no filter = every row (used by tests and the "all" view).
        (await store.GetRangeAsync(0, 100)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Count_ScopedToWorkspace()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://a/1", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://b/1", 200, 1, null, null, workspaceId: "/ws/B");
        await store.AppendAsync("GET", "https://b/2", 200, 1, null, null, workspaceId: "/ws/B");

        (await store.CountAsync("/ws/A")).Should().Be(1);
        (await store.CountAsync("/ws/B")).Should().Be(2);
        (await store.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Clear_ScopedToWorkspace_LeavesOtherWorkspacesIntact()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://a/1", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://b/1", 200, 1, null, null, workspaceId: "/ws/B");

        await store.ClearAsync("/ws/A");

        (await store.CountAsync("/ws/A")).Should().Be(0);
        (await store.CountAsync("/ws/B")).Should().Be(1);
    }

    [Fact]
    public async Task CountPrune_IsPerWorkspace_OneBusyWorkspaceDoesNotEvictAnother()
    {
        var store = Store();
        store.MaxRetained = 3;

        // Workspace B stays small; workspace A overflows. A's overflow must not touch B.
        await store.AppendAsync("GET", "https://b/keep", 200, 1, null, null, workspaceId: "/ws/B");
        for (int i = 0; i < 10; i++)
            await store.AppendAsync("GET", $"https://a/{i}", 200, i, null, null, workspaceId: "/ws/A");

        (await store.CountAsync("/ws/A")).Should().Be(3); // capped
        (await store.CountAsync("/ws/B")).Should().Be(1); // untouched

        var b = await store.GetRangeAsync(0, 100, workspaceId: "/ws/B");
        b.Should().ContainSingle(r => r.Url == "https://b/keep");
    }

    [Fact]
    public async Task Search_FiltersByMethodOrUrl_CaseInsensitive()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://api.example.com/users", 200, 1, null, null);
        await store.AppendAsync("POST", "https://api.example.com/orders", 201, 1, null, null);
        await store.AppendAsync("DELETE", "https://cdn.other.com/asset", 204, 1, null, null);

        (await store.GetRangeAsync(0, 100, search: "USERS"))
            .Select(r => r.Url).Should().BeEquivalentTo("https://api.example.com/users");

        (await store.GetRangeAsync(0, 100, search: "example.com"))
            .Should().HaveCount(2);

        (await store.GetRangeAsync(0, 100, search: "post"))
            .Select(r => r.Method).Should().BeEquivalentTo("POST");

        (await store.GetRangeAsync(0, 100, search: "nomatch")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_TreatsWildcardsLiterally()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://x/a", 200, 1, null, null);
        await store.AppendAsync("GET", "https://x/100%done", 200, 1, null, null);

        // A literal % must match only the row that actually contains it, not act as a wildcard.
        var hits = await store.GetRangeAsync(0, 100, search: "100%");
        hits.Should().ContainSingle(r => r.Url == "https://x/100%done");
    }

    [Fact]
    public async Task Search_CombinesWithWorkspaceFilter()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://api/users", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://api/users", 200, 1, null, null, workspaceId: "/ws/B");

        var hits = await store.GetRangeAsync(0, 100, workspaceId: "/ws/A", search: "users");
        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_AssignsLegacyNullRowsToWorkspace_Idempotent()
    {
        var store = Store();
        // Legacy rows recorded before per-workspace scoping have no workspace_id.
        await store.AppendAsync("GET", "https://legacy/1", 200, 1, null, null);
        await store.AppendAsync("GET", "https://legacy/2", 200, 1, null, null);
        // A row already scoped to another workspace must be left alone.
        await store.AppendAsync("GET", "https://b/1", 200, 1, null, null, workspaceId: "/ws/B");

        var moved = await store.BackfillWorkspaceAsync("/ws/A");
        moved.Should().Be(2);

        (await store.CountAsync("/ws/A")).Should().Be(2);
        (await store.CountAsync("/ws/B")).Should().Be(1);

        // Running again moves nothing — every row now has a workspace.
        (await store.BackfillWorkspaceAsync("/ws/A")).Should().Be(0);
    }

    [Fact]
    public async Task GetRecent_ScopedToWorkspace()
    {
        var store = Store();
        await store.AppendAsync("GET", "https://a/1", 200, 1, null, null, workspaceId: "/ws/A");
        await store.AppendAsync("GET", "https://b/1", 200, 1, null, null, workspaceId: "/ws/B");

        var a = await store.GetRecentAsync(limit: 100, workspaceId: "/ws/A");
        a.Should().ContainSingle(r => r.Url == "https://a/1");
    }
}
