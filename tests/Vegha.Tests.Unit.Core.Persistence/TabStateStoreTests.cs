using Microsoft.Data.Sqlite;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class TabStateStoreTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "vegha-tabs-" + Guid.NewGuid().ToString("N") + ".db");

    // The connection pool keeps the .db file handle open after the store's connections are
    // disposed, so the temp file can't be deleted until the pool is cleared.
    private static void Cleanup(string db)
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(db); } catch { /* best-effort */ }
    }

    private static TabStateRow Row(string id, bool dirty = false, bool scratch = false, string? blob = null) =>
        new(Id: id, WorkspaceId: "/ws/a", CollectionPath: scratch ? null : "/A",
            SourcePath: scratch ? null : "/A/" + id + ".bru", Name: id, Kind: "Http",
            OrderIndex: 0, IsActive: false, IsDirty: dirty, IsScratch: scratch, StateBlob: blob);

    [Fact]
    public void EmptyOnFirstUse()
    {
        var db = TempDb();
        try { new TabStateStore(db).LoadAll().Should().BeEmpty(); }
        finally { Cleanup(db); }
    }

    [Fact]
    public void SaveAll_Then_LoadAll_RoundTrips_AllFields_InOrder()
    {
        var db = TempDb();
        try
        {
            var store = new TabStateStore(db);
            var rows = new[]
            {
                Row("a") with { OrderIndex = 0, IsActive = true },
                Row("b", dirty: true, blob: "{\"method\":\"POST\"}") with { OrderIndex = 1 },
                Row("scratch1", scratch: true, blob: "{\"name\":\"Untitled\"}") with { OrderIndex = 2 },
            };
            store.SaveAll(rows);

            var loaded = store.LoadAll();
            loaded.Should().HaveCount(3);
            loaded.Select(r => r.Id).Should().ContainInOrder("a", "b", "scratch1");

            var a = loaded[0];
            a.IsActive.Should().BeTrue();
            a.StateBlob.Should().BeNull();

            var b = loaded[1];
            b.IsDirty.Should().BeTrue();
            b.StateBlob.Should().Be("{\"method\":\"POST\"}");

            var s = loaded[2];
            s.IsScratch.Should().BeTrue();
            s.SourcePath.Should().BeNull();
            s.CollectionPath.Should().BeNull();
            s.WorkspaceId.Should().Be("/ws/a");
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public void SaveAll_ReplacesEntireSet()
    {
        var db = TempDb();
        try
        {
            var store = new TabStateStore(db);
            store.SaveAll(new[] { Row("a"), Row("b") });
            store.SaveAll(new[] { Row("c") });

            store.LoadAll().Select(r => r.Id).Should().BeEquivalentTo(new[] { "c" });
        }
        finally { Cleanup(db); }
    }
}
