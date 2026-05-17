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
