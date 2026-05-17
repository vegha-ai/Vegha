using System.IO;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

/// <summary>The bootstrapper auto-creates the default workspace folder + workspace.yml on
/// first launch. These tests run against a temp store + folder so we don't perturb the
/// real <c>%AppData%/Roaming/Vegha/default-workspace</c>.</summary>
public class WorkspaceBootstrapperTests : IDisposable
{
    private readonly string _tmpStorePath = Path.Combine(
        Path.GetTempPath(), "Vegha-test-" + Guid.NewGuid().ToString("N"), "workspaces.json");
    private readonly string _tmpWsFolder = Path.Combine(
        Path.GetTempPath(), "Vegha-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_tmpStorePath)!, recursive: true); } catch { }
        try { Directory.Delete(_tmpWsFolder, recursive: true); } catch { }
    }

    [Fact]
    public void EnsureDefault_CreatesFolder_Manifest_AndRegistersAsDefault()
    {
        var store = new WorkspaceStore(_tmpStorePath);
        var ws = WorkspaceBootstrapper.EnsureDefaultWorkspace(store, _tmpWsFolder);

        Directory.Exists(_tmpWsFolder).Should().BeTrue();
        Directory.Exists(Path.Combine(_tmpWsFolder, "collections")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tmpWsFolder, "environments")).Should().BeTrue();
        WorkspaceManifestIO.Exists(_tmpWsFolder).Should().BeTrue();

        ws.IsDefault.Should().BeTrue();
        ws.FolderPath.Should().Be(_tmpWsFolder);
    }

    [Fact]
    public void EnsureDefault_IsIdempotent()
    {
        var store = new WorkspaceStore(_tmpStorePath);
        var first = WorkspaceBootstrapper.EnsureDefaultWorkspace(store, _tmpWsFolder);
        var second = WorkspaceBootstrapper.EnsureDefaultWorkspace(store, _tmpWsFolder);

        first.FolderPath.Should().Be(second.FolderPath);
        var state = store.Load();
        state.Workspaces.Count(w => w.IsDefault).Should().Be(1);
    }

    [Fact]
    public void EnsureDefault_AfterRemoval_RecreatesFolder()
    {
        var store = new WorkspaceStore(_tmpStorePath);
        WorkspaceBootstrapper.EnsureDefaultWorkspace(store, _tmpWsFolder);

        // Simulate the user manually deleting the folder behind the app's back.
        Directory.Delete(_tmpWsFolder, recursive: true);
        WorkspaceBootstrapper.EnsureDefaultWorkspace(store, _tmpWsFolder);

        Directory.Exists(_tmpWsFolder).Should().BeTrue();
    }
}
