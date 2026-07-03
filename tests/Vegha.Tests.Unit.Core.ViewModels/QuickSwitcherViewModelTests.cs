using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers the quick-switcher rows (the current workspace's open set, MRU order),
/// initial selection (second row for tap-to-toggle), wrap-around movement, and commit.</summary>
public class QuickSwitcherViewModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "vegha-qs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private (CollectionsViewModel collections, WorkspacesViewModel workspaces) Build(params string[] collectionNames)
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

        foreach (var n in collectionNames)
        {
            collections.AvailableCollections.Add(new CollectionRootViewModel
            {
                Name = n,
                SourcePath = Path.Combine(_tmp, n),
                Collection = new Collection { Name = n },
            });
        }
        return (collections, workspaces);
    }

    /// <summary>Marks the given collections open (MRU order = argument order).</summary>
    private void Open(WorkspacesViewModel workspaces, params string[] names)
    {
        var ws = workspaces.ActiveWorkspace!;
        ws.OpenCollectionPaths.Clear();
        ws.OpenCollectionPaths.AddRange(names.Select(n => Path.Combine(_tmp, n)));
    }

    [Fact]
    public void Rows_Are_OpenSet_InMruOrder()
    {
        var (collections, workspaces) = Build("A", "B", "C", "D");
        // Only A, C, B are open; D is not — the switcher must list exactly the open set in order.
        Open(workspaces, "C", "A", "B");
        var qs = new QuickSwitcherViewModel(collections, workspaces);
        qs.Rows.Select(r => r.Name).Should().ContainInOrder("C", "A", "B");
        qs.Rows.Select(r => r.Name).Should().NotContain("D");
    }

    [Fact]
    public void InitialSelection_IsSecondRow_ForTapToggle()
    {
        var (collections, workspaces) = Build("A", "B", "C");
        Open(workspaces, "A", "B", "C");
        var qs = new QuickSwitcherViewModel(collections, workspaces);
        qs.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void InitialSelection_IsZero_WithSingleOpenCollection()
    {
        var (collections, workspaces) = Build("Solo", "Other");
        Open(workspaces, "Solo");
        var qs = new QuickSwitcherViewModel(collections, workspaces);
        qs.Rows.Should().ContainSingle();
        qs.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Move_Wraps_BothDirections()
    {
        var (collections, workspaces) = Build("A", "B", "C");
        Open(workspaces, "A", "B", "C");
        var qs = new QuickSwitcherViewModel(collections, workspaces);
        qs.SelectedIndex = 2;
        qs.Move(+1);
        qs.SelectedIndex.Should().Be(0); // wrapped forward
        qs.Move(-1);
        qs.SelectedIndex.Should().Be(2); // wrapped back
    }

    [Fact]
    public void Commit_SwitchesActiveCollection()
    {
        var (collections, workspaces) = Build("A", "B");
        Open(workspaces, "A", "B");
        var qs = new QuickSwitcherViewModel(collections, workspaces);
        qs.SelectedIndex = qs.Rows.ToList().FindIndex(r => r.Name == "B");
        qs.Commit();
        collections.ActiveCollection!.Name.Should().Be("B");
    }
}
