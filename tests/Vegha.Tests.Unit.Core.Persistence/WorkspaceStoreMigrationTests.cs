using System.IO;
using System.Text.Json;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

/// <summary>Validates that a legacy <c>workspaces.json</c> (schemaVersion missing or 0/1)
/// is upgraded to the current schema (v3) with per-collection expansion buckets and
/// per-collection active-env memory, non-destructively.</summary>
public class WorkspaceStoreMigrationTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "Vegha-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public WorkspaceStoreMigrationTests()
    {
        Directory.CreateDirectory(_tmpDir);
        _path = Path.Combine(_tmpDir, "workspaces.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void LegacyV1Payload_MigratesTo_CurrentSchema_WithBucketedExpansionPaths()
    {
        // Legacy single-folder workspace with a flat ExpandedPaths list — what an older
        // version of the app would have written.
        const string legacyJson = """
            {
              "Workspaces": [
                {
                  "Name": "OldWs",
                  "FolderPath": "C:/legacy/ws",
                  "ExpandedPaths": ["C:/legacy/ws/folder-a", "C:/legacy/ws/folder-b"]
                }
              ],
              "ActiveIndex": 0
            }
            """;
        File.WriteAllText(_path, legacyJson);

        var store = new WorkspaceStore(_path);
        var state = store.Load();

        // V1 → V2 (per-collection expansion buckets) → V3 (active-collection fields) →
        // V4 (per-workspace ActiveGlobalEnvironmentName). Each migration is a non-destructive
        // default-initializer for the new field(s).
        state.SchemaVersion.Should().Be(4);
        state.Workspaces.Should().ContainSingle();
        var ws = state.Workspaces[0];
        ws.Name.Should().Be("OldWs");
        ws.ExpandedPaths.Should().BeEmpty(); // legacy field flushed
        ws.ExpandedPathsByCollection.Should().ContainKey("C:/legacy/ws");
        ws.ExpandedPathsByCollection["C:/legacy/ws"]
            .Should().BeEquivalentTo(new[] { "C:/legacy/ws/folder-a", "C:/legacy/ws/folder-b" });
        ws.ActiveCollectionPath.Should().BeNull();
        ws.ActiveEnvironmentByCollection.Should().BeEmpty();
        ws.ActiveGlobalEnvironmentName.Should().BeNull();
    }

    [Fact]
    public void CurrentSchemaPayload_RoundTrips()
    {
        var native = new WorkspaceState
        {
            SchemaVersion = 4,
            Workspaces = new List<Workspace>
            {
                new("W", "C:/ws") { IsDefault = true }
            },
            ActiveIndex = 0,
        };
        var store = new WorkspaceStore(_path);
        store.Save(native);

        var loaded = store.Load();
        loaded.SchemaVersion.Should().Be(4);
        loaded.Workspaces.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
    }
}
