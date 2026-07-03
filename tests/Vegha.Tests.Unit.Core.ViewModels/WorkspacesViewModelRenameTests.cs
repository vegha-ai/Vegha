using Vegha.App.ViewModels;
using Vegha.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers workspace rename (manifest + workspaces.json persistence) and the
/// non-loading collection enumeration used by the picker's "Other workspaces" section.</summary>
public class WorkspacesViewModelRenameTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "vegha-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private (WorkspacesViewModel vm, WorkspaceStore store) NewVm()
    {
        Directory.CreateDirectory(_tmp);
        var editor = new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
        var collections = new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);
        var store = new WorkspaceStore(Path.Combine(_tmp, "workspaces.json"));
        var vm = new WorkspacesViewModel(store, collections, NullLogger<WorkspacesViewModel>.Instance);
        return (vm, store);
    }

    [Fact]
    public void RenameWorkspace_UpdatesManifest_And_PersistsRegistry()
    {
        var (vm, store) = NewVm();
        var folder = Path.Combine(_tmp, "wsA");
        vm.CreateWorkspace("Original", folder);
        var ws = vm.Workspaces.First(w => string.Equals(w.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

        var ok = vm.RenameWorkspace(ws, "Renamed");

        ok.Should().BeTrue();
        ws.Name.Should().Be("Renamed");
        // Manifest on disk carries the new name (identity is by manifest, not folder name).
        WorkspaceManifestIO.Read(folder)!.Name.Should().Be("Renamed");
        // Registry persisted the new name — a reload sees it.
        store.Load().Workspaces.Should().Contain(w => w.Name == "Renamed");
    }

    [Fact]
    public void RenameWorkspace_NoOp_WhenSameName()
    {
        var (vm, _) = NewVm();
        var folder = Path.Combine(_tmp, "wsB");
        vm.CreateWorkspace("Same", folder);
        var ws = vm.Workspaces.First(w => string.Equals(w.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

        vm.RenameWorkspace(ws, "Same").Should().BeFalse();
        vm.RenameWorkspace(ws, "   ").Should().BeFalse();
    }

    [Fact]
    public void EnumerateWorkspaceCollections_ResolvesNames_FromCollectionBru()
    {
        var (vm, _) = NewVm();
        var folder = Path.Combine(_tmp, "wsC");
        vm.CreateWorkspace("C", folder);
        var ws = vm.Workspaces.First(w => string.Equals(w.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

        var colRoot = Path.Combine(folder, "collections");
        var apiDir = Path.Combine(colRoot, "ApiDir");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "collection.bru"), "meta {\n  name: Pretty Api\n  type: collection\n}\n");
        var plainDir = Path.Combine(colRoot, "PlainFolder");
        Directory.CreateDirectory(plainDir); // no collection.bru → name falls back to folder

        var refs = vm.EnumerateWorkspaceCollections(ws);

        refs.Should().Contain(r => r.Name == "Pretty Api" && r.Path.EndsWith("ApiDir"));
        refs.Should().Contain(r => r.Name == "PlainFolder");
        refs.Should().OnlyContain(r => ReferenceEquals(r.Workspace, ws));
    }
}
