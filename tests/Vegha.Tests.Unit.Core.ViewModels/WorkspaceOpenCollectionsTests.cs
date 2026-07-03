using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers the per-workspace "open collections" set: activation opens (MRU, capped at
/// 5, LRU evicted), and closing removes from the set without unlinking, reactivating another.</summary>
public class WorkspaceOpenCollectionsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "vegha-open-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private (CollectionsViewModel collections, WorkspacesViewModel workspaces, Dictionary<string, CollectionRootViewModel> roots)
        Build(params string[] names)
    {
        Directory.CreateDirectory(_tmp);
        var editor = new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
        var collections = new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);
        var store = new WorkspaceStore(Path.Combine(_tmp, "workspaces.json"));
        var workspaces = new WorkspacesViewModel(store, collections, NullLogger<WorkspacesViewModel>.Instance);
        // Ensure there's an active workspace to hold the open set.
        workspaces.CreateWorkspace("W", Path.Combine(_tmp, "wsfolder"));

        var roots = new Dictionary<string, CollectionRootViewModel>();
        foreach (var n in names)
        {
            var r = new CollectionRootViewModel
            {
                Name = n,
                SourcePath = Path.Combine(_tmp, n),
                Collection = new Collection { Name = n },
            };
            collections.AvailableCollections.Add(r);
            roots[n] = r;
        }
        return (collections, workspaces, roots);
    }

    [Fact]
    public void Activating_Collections_BuildsMruOpenSet()
    {
        var (collections, workspaces, roots) = Build("A", "B", "C");
        collections.ActiveCollection = roots["A"];
        collections.ActiveCollection = roots["B"];
        collections.ActiveCollection = roots["C"];

        // MRU: most-recently activated first.
        workspaces.ActiveWorkspace!.OpenCollectionPaths
            .Should().ContainInOrder(roots["C"].SourcePath, roots["B"].SourcePath, roots["A"].SourcePath);
    }

    [Fact]
    public void OpenSet_CapsAtFive_EvictingLeastRecent()
    {
        var (collections, workspaces, roots) = Build("A", "B", "C", "D", "E", "F");
        foreach (var n in new[] { "A", "B", "C", "D", "E", "F" })
            collections.ActiveCollection = roots[n];

        var open = workspaces.ActiveWorkspace!.OpenCollectionPaths;
        open.Should().HaveCount(WorkspacesViewModel.MaxOpenCollections);
        // A (least-recent) was evicted; F (most-recent) is first.
        open.Should().NotContain(roots["A"].SourcePath);
        open[0].Should().Be(roots["F"].SourcePath);
    }

    [Fact]
    public void CloseCollection_RemovesFromOpenSet_KeepsLinked_ReactivatesNext()
    {
        var (collections, workspaces, roots) = Build("A", "B", "C");
        collections.ActiveCollection = roots["A"];
        collections.ActiveCollection = roots["B"];
        collections.ActiveCollection = roots["C"]; // open set: [C, B, A], active = C

        workspaces.CloseCollection(roots["C"].SourcePath);

        var ws = workspaces.ActiveWorkspace!;
        ws.OpenCollectionPaths.Should().NotContain(roots["C"].SourcePath);
        // Still available (closing doesn't unlink).
        collections.AvailableCollections.Should().Contain(c => c.SourcePath == roots["C"].SourcePath);
        // Active moved to the next open collection (B).
        collections.ActiveCollection!.Name.Should().Be("B");
    }

    [Fact]
    public void CloseCollection_NonActive_LeavesActiveAlone()
    {
        var (collections, workspaces, roots) = Build("A", "B");
        collections.ActiveCollection = roots["A"];
        collections.ActiveCollection = roots["B"]; // active = B, open [B, A]

        workspaces.CloseCollection(roots["A"].SourcePath);

        collections.ActiveCollection!.Name.Should().Be("B"); // unchanged
        workspaces.ActiveWorkspace!.OpenCollectionPaths.Should().NotContain(roots["A"].SourcePath);
    }
}
