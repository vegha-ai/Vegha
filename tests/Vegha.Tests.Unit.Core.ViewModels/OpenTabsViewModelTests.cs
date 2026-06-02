using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class OpenTabsViewModelTests
{
    private static OpenTabsViewModel NewVm()
    {
        // Each call returns a fresh editor — the production wiring uses the DI factory.
        Func<RequestEditorViewModel> factory = () =>
            new RequestEditorViewModel(
                executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
                oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
                scriptHost: new Vegha.Core.Scripting.JintHost(),
                logger: NullLogger<RequestEditorViewModel>.Instance);
        return new OpenTabsViewModel(factory, NullLogger<OpenTabsViewModel>.Instance);
    }

    private static RequestItem MakeRequest(string name = "r", string method = "GET", string url = "https://x.test/")
        => new() { Name = name, Method = method, Url = url, Kind = RequestKind.Http };

    [Fact]
    public void OpenOrActivate_AddsNewTab_AndActivatesIt()
    {
        var vm = NewVm();
        var tab = vm.OpenOrActivate(MakeRequest("a"), "/path/a.req.json");

        vm.Tabs.Should().ContainSingle();
        vm.ActiveTab.Should().Be(tab);
        vm.ActiveTab!.IsActive.Should().BeTrue();
    }

    [Fact]
    public void OpenOrActivate_SecondTimeWithSameSource_ActivatesExistingTab_NoDuplicates()
    {
        var vm = NewVm();
        var first = vm.OpenOrActivate(MakeRequest("a"), "/path/a.req.json");
        var draft = vm.OpenDraft();
        vm.ActiveTab.Should().Be(draft);

        var second = vm.OpenOrActivate(MakeRequest("a-edited"), "/path/a.req.json");

        second.Should().BeSameAs(first);
        vm.Tabs.Should().HaveCount(2);
        vm.ActiveTab.Should().Be(first);
    }

    [Fact]
    public void OpenDraft_AddsBlankTab_WithoutSourcePath()
    {
        var vm = NewVm();
        var tab = vm.OpenDraft();
        vm.Tabs.Should().ContainSingle();
        tab.SourcePath.Should().BeNull();
        tab.Id.Should().StartWith("draft:");
    }

    [Fact]
    public void CloseTab_PicksNeighbor_AsNewActive()
    {
        var vm = NewVm();
        var t1 = vm.OpenOrActivate(MakeRequest("a"), "/path/a.req.json");
        var t2 = vm.OpenOrActivate(MakeRequest("b"), "/path/b.req.json");
        var t3 = vm.OpenOrActivate(MakeRequest("c"), "/path/c.req.json");
        vm.ActiveTab.Should().Be(t3);

        vm.CloseTab(t2);
        vm.Tabs.Should().HaveCount(2);
        vm.ActiveTab.Should().Be(t3); // not the closed one

        vm.CloseTab(t3);
        vm.ActiveTab.Should().Be(t1); // last remaining
    }

    [Fact]
    public void CloseTab_WhenLast_SetsActiveToNull()
    {
        var vm = NewVm();
        var t = vm.OpenDraft();
        vm.CloseTab(t);
        vm.ActiveTab.Should().BeNull();
        vm.Tabs.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_ProducesEntries_WithActiveFlag()
    {
        var vm = NewVm();
        var t1 = vm.OpenOrActivate(MakeRequest("a"), "/path/a.req.json");
        var t2 = vm.OpenOrActivate(MakeRequest("b"), "/path/b.req.json");
        vm.ActiveTab = t1;

        var snap = vm.Snapshot();
        snap.Should().HaveCount(2);
        snap.Single(s => s.SourcePath == "/path/a.req.json").IsActive.Should().BeTrue();
        snap.Single(s => s.SourcePath == "/path/b.req.json").IsActive.Should().BeFalse();
    }

    [Fact]
    public void TabsChangedEvent_Fires_OnAddAndClose()
    {
        var vm = NewVm();
        var fires = 0;
        vm.TabsChanged += (_, _) => fires++;

        vm.OpenDraft();
        vm.OpenDraft();
        vm.CloseTab(vm.Tabs[0]);

        fires.Should().Be(3);
    }

    [Fact]
    public void Activate_SwitchesActiveTab_WhenInList()
    {
        var vm = NewVm();
        var t1 = vm.OpenOrActivate(MakeRequest("a"), "/path/a.req.json");
        var t2 = vm.OpenOrActivate(MakeRequest("b"), "/path/b.req.json");
        vm.ActiveTab.Should().Be(t2);

        vm.ActivateCommand.Execute(t1);
        vm.ActiveTab.Should().Be(t1);
        t1.IsActive.Should().BeTrue();
        t2.IsActive.Should().BeFalse();
    }

    [Fact]
    public void OpenDraft_DefaultsCollectionPath_ToActiveScope()
    {
        // A draft created while a collection is active should belong to that scope, not float
        // into every collection's tab strip (the original cross-collection leak).
        var vm = NewVm();
        vm.OpenOrActivate(MakeRequest("a"), "/A/a.bru", collectionPath: "/A");
        vm.ActiveScope = "/A";

        var draft = vm.OpenDraft();
        draft.CollectionPath.Should().Be("/A");
    }

    [Fact]
    public void CreateScratch_TagsWorkspace_AndScopesVisibility()
    {
        var vm = NewVm();
        vm.ActiveWorkspaceId = "/ws/a";
        var s = vm.CreateScratch("/ws/a");

        s.IsScratch.Should().BeTrue();
        s.WorkspaceId.Should().Be("/ws/a");
        s.Name.Should().Be("Untitled");
        vm.VisibleTabs.Should().Contain(s);

        // Switching to another workspace hides the scratch tab (no cross-workspace leak).
        vm.ActiveWorkspaceId = "/ws/b";
        vm.VisibleTabs.Should().NotContain(s);
    }

    [Fact]
    public void SavingDirtyScratch_EnablesSave_AndRaisesSaveAsRequested()
    {
        var vm = NewVm();
        vm.ActiveWorkspaceId = "/ws/a";
        var s = vm.CreateScratch("/ws/a");

        s.Editor.SaveCommand.CanExecute(null).Should().BeFalse("a pristine draft has nothing to save");

        s.Editor.Url = "https://typed/"; // marks the editor dirty
        s.Editor.IsDirty.Should().BeTrue();
        s.Editor.SaveCommand.CanExecute(null).Should().BeTrue("a dirty draft can be saved (promoted)");

        HttpRequestTabViewModel? promoted = null;
        vm.SaveAsRequested += (_, t) => promoted = t;
        s.Editor.SaveCommand.Execute(null);

        promoted.Should().BeSameAs(s, "saving a fileless draft asks the host to promote it to a collection");
    }

    [Fact]
    public void WorkspaceSwitch_RestoresLastActiveScratchTab()
    {
        var vm = NewVm();
        vm.ActiveWorkspaceId = "/ws/a";
        vm.CreateScratch("/ws/a");                 // Untitled
        var u2 = vm.CreateScratch("/ws/a");        // Untitled 2 — now active
        vm.ActiveTab.Should().Be(u2);

        // Leave to another workspace and come back.
        vm.ActiveWorkspaceId = "/ws/b";
        vm.ActiveWorkspaceId = "/ws/a";

        vm.ActiveTab.Should().Be(u2, "returning to a workspace re-selects the tab that was active there");
    }

    [Fact]
    public void CreateScratch_IncrementsUntitledNames_PerWorkspace()
    {
        var vm = NewVm();
        vm.ActiveWorkspaceId = "/ws/a";
        vm.CreateScratch("/ws/a").Name.Should().Be("Untitled");
        vm.CreateScratch("/ws/a").Name.Should().Be("Untitled 2");
        // A different workspace numbers independently.
        vm.CreateScratch("/ws/b").Name.Should().Be("Untitled");
    }

    [Fact]
    public void FullSnapshot_BlobsDirtyAndScratch_NotCleanFileTabs()
    {
        var vm = NewVm();
        vm.ActiveWorkspaceId = "/ws/a";
        var scratch = vm.CreateScratch("/ws/a");
        var clean = vm.OpenOrActivate(MakeRequest("a"), "/A/a.bru", collectionPath: "/A");
        var dirty = vm.OpenOrActivate(MakeRequest("b"), "/A/b.bru", collectionPath: "/A");
        ((HttpRequestTabViewModel)dirty).Editor.IsDirty = true;

        var rows = vm.FullSnapshot();
        rows.Single(r => r.Id == scratch.Id).StateBlob.Should().NotBeNullOrEmpty("scratch tabs persist full state");
        rows.Single(r => r.Id == dirty.Id).StateBlob.Should().NotBeNullOrEmpty("dirty tabs persist full state");
        rows.Single(r => r.Id == clean.Id).StateBlob.Should().BeNull("clean file tabs restore from disk");
    }

    [Fact]
    public void RestoreHttpTab_ReinstatesDirtyState_AndName()
    {
        var vm = NewVm();
        var item = MakeRequest("orig", url: "https://restored/");
        var tab = vm.RestoreHttpTab(item, id: "scratch:1", sourcePath: null, collectionPath: null,
            workspaceId: "/ws/a", isScratch: true, isDirty: true, name: "My Draft");

        tab.Name.Should().Be("My Draft");
        tab.IsScratch.Should().BeTrue();
        tab.IsDirty.Should().BeTrue();
        ((HttpRequestTabViewModel)tab).Editor.Url.Should().Be("https://restored/");
    }

    [Fact]
    public void CloseToLeft_ClosesVisibleTabsBefore_KeepingTabActive()
    {
        var vm = NewVm();
        vm.OpenOrActivate(MakeRequest("a"), "/A/a.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("b"), "/A/b.bru", collectionPath: "/A");
        var c = vm.OpenOrActivate(MakeRequest("c"), "/A/c.bru", collectionPath: "/A");
        vm.ActiveScope = "/A";

        vm.CloseToLeft(c);

        vm.VisibleTabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "c" });
        vm.ActiveTab.Should().Be(c);
    }

    [Fact]
    public void CloseToRight_ClosesVisibleTabsAfter_KeepingTabActive()
    {
        var vm = NewVm();
        var a = vm.OpenOrActivate(MakeRequest("a"), "/A/a.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("b"), "/A/b.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("c"), "/A/c.bru", collectionPath: "/A");
        vm.ActiveScope = "/A";

        vm.CloseToRight(a);

        vm.VisibleTabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "a" });
        vm.ActiveTab.Should().Be(a);
    }

    [Fact]
    public void CloseSaved_ClosesCleanTabs_KeepsDirty()
    {
        var vm = NewVm();
        vm.OpenOrActivate(MakeRequest("a"), "/A/a.bru", collectionPath: "/A");
        var b = vm.OpenOrActivate(MakeRequest("b"), "/A/b.bru", collectionPath: "/A");
        vm.ActiveScope = "/A";
        b.IsDirty = true;

        vm.CloseSaved();

        vm.VisibleTabs.Should().ContainSingle().Which.Should().Be(b);
    }

    [Fact]
    public void CloseOthers_StaysWithinScope_LeavesOtherCollectionsTabs()
    {
        var vm = NewVm();
        var a1 = vm.OpenOrActivate(MakeRequest("a1"), "/A/a1.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("a2"), "/A/a2.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("b1"), "/B/b1.bru", collectionPath: "/B");
        vm.ActiveScope = "/A";

        vm.CloseOthers(a1);

        vm.Tabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "a1", "b1" });
        vm.ActiveTab.Should().Be(a1);
    }

    [Fact]
    public void CloseAll_OnlyClosesVisibleScope()
    {
        var vm = NewVm();
        vm.OpenOrActivate(MakeRequest("a1"), "/A/a1.bru", collectionPath: "/A");
        vm.OpenOrActivate(MakeRequest("b1"), "/B/b1.bru", collectionPath: "/B");
        vm.ActiveScope = "/A";

        vm.CloseAll();

        vm.Tabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "b1" });
    }

    [Fact]
    public async Task LoadFromStoreAsync_RestoresTabs_AndActiveSelection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pathA = Path.Combine(dir, "a.bru");
            var pathB = Path.Combine(dir, "b.bru");
            File.WriteAllText(pathA, "");
            File.WriteAllText(pathB, "");

            var vm = NewVm();
            var snapshots = new List<TabSnapshot>
            {
                new("a", pathA, "A", RequestKind.Http, IsActive: false),
                new("b", pathB, "B", RequestKind.Http, IsActive: true),
            };

            var opened = await vm.LoadFromStoreAsync(snapshots,
                p => Task.FromResult<RequestItem?>(MakeRequest(Path.GetFileNameWithoutExtension(p))));

            opened.Should().Be(2);
            vm.Tabs.Select(t => t.SourcePath).Should().BeEquivalentTo(new[] { pathA, pathB });
            vm.ActiveTab!.SourcePath.Should().Be(pathB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoadFromStoreAsync_SilentlyDropsEntries_WithMissingFiles()
    {
        var vm = NewVm();
        var snapshots = new List<TabSnapshot>
        {
            new("dead", "/does/not/exist.bru", "Dead", RequestKind.Http, IsActive: false),
        };

        var opened = await vm.LoadFromStoreAsync(snapshots,
            _ => Task.FromResult<RequestItem?>(MakeRequest()));

        opened.Should().Be(0);
        vm.Tabs.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadFromStoreAsync_HandlesResolverFailure_WithoutCrashing()
    {
        var path = Path.Combine(Path.GetTempPath(), "vegha-bad-" + Guid.NewGuid().ToString("N") + ".bru");
        File.WriteAllText(path, "");
        try
        {
            var vm = NewVm();
            var snapshots = new List<TabSnapshot>
            {
                new("x", path, "X", RequestKind.Http, IsActive: true),
            };

            var opened = await vm.LoadFromStoreAsync(snapshots,
                _ => throw new FormatException("bad bru"));

            opened.Should().Be(0);
            vm.Tabs.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }
}
