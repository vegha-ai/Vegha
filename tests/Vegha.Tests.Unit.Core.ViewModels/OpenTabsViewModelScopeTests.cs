using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers the per-collection scope filter on <see cref="OpenTabsViewModel"/>:
/// tabs from non-active collections are hidden via <c>VisibleTabs</c>, and the last-active
/// tab in each scope is restored on switch.</summary>
public class OpenTabsViewModelScopeTests
{
    private static OpenTabsViewModel NewVm()
    {
        Func<RequestEditorViewModel> factory = () => new RequestEditorViewModel(
            executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);
        return new OpenTabsViewModel(factory, NullLogger<OpenTabsViewModel>.Instance);
    }

    private static RequestItem Req(string name) =>
        new() { Name = name, Method = "GET", Url = "https://x.test/", Kind = RequestKind.Http };

    [Fact]
    public void OpenOrActivate_StampsCollectionPathOnTab()
    {
        var vm = NewVm();
        var tab = vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");
        tab.CollectionPath.Should().Be("/A");
    }

    [Fact]
    public void VisibleTabs_FiltersToActiveScope()
    {
        var vm = NewVm();
        vm.OpenOrActivate(Req("a1"), "/A/a1.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("a2"), "/A/a2.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("b1"), "/B/b1.req.json", collectionPath: "/B");

        vm.ActiveScope = "/A";
        vm.VisibleTabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "a1", "a2" });

        vm.ActiveScope = "/B";
        vm.VisibleTabs.Select(t => t.Name).Should().BeEquivalentTo(new[] { "b1" });
    }

    [Fact]
    public void UntaggedTabs_VisibleInEveryScope()
    {
        // Drafts created before any collection is active have no CollectionPath. The user
        // shouldn't lose them when scope changes — they're shown in every scope.
        var vm = NewVm();
        vm.OpenDraft();                                       // untagged
        vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");

        vm.ActiveScope = "/B";
        vm.VisibleTabs.Should().HaveCount(1);
        vm.VisibleTabs[0].CollectionPath.Should().BeNull();
    }

    [Fact]
    public void NullScope_HidesTaggedTabs_KeepsOnlyUntagged()
    {
        // Workspace-with-no-collections case: ActiveScope falls back to null. Tagged tabs
        // from the previously-active scope must NOT remain visible; untagged drafts may.
        var vm = NewVm();
        vm.OpenDraft();                                                                          // untagged
        vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("b"), "/B/b.req.json", collectionPath: "/B");

        vm.ActiveScope = null;
        vm.VisibleTabs.Should().ContainSingle()
            .Which.CollectionPath.Should().BeNull();
    }

    [Fact]
    public void Switching_Scopes_Restores_LastActiveTab()
    {
        var vm = NewVm();
        var a1 = vm.OpenOrActivate(Req("a1"), "/A/a1.req.json", collectionPath: "/A");
        var a2 = vm.OpenOrActivate(Req("a2"), "/A/a2.req.json", collectionPath: "/A");
        vm.ActiveScope = "/A";
        vm.ActiveTab.Should().Be(a2);  // a2 is the last-active after open

        vm.OpenOrActivate(Req("b1"), "/B/b1.req.json", collectionPath: "/B");
        vm.ActiveScope = "/B";

        // Coming back to /A should restore a2 (the remembered active).
        vm.ActiveScope = "/A";
        vm.ActiveTab.Should().Be(a2);
    }
}
