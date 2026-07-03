using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers Ctrl+Tab / Ctrl+Shift+Tab tab cycling over the VISIBLE (scope-filtered)
/// tab list, with wrap-around.</summary>
public class OpenTabsViewModelSwitchTests
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
    public void ActivateNext_CyclesForward_WithWrap()
    {
        var vm = NewVm();
        vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("b"), "/A/b.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("c"), "/A/c.req.json", collectionPath: "/A");
        vm.ActiveScope = "/A";
        vm.ActiveTab = vm.VisibleTabs[0]; // "a"

        vm.ActivateNextTab();
        vm.ActiveTab!.Name.Should().Be("b");
        vm.ActivateNextTab();
        vm.ActiveTab!.Name.Should().Be("c");
        vm.ActivateNextTab();
        vm.ActiveTab!.Name.Should().Be("a"); // wrapped
    }

    [Fact]
    public void ActivatePrevious_CyclesBackward_WithWrap()
    {
        var vm = NewVm();
        vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("b"), "/A/b.req.json", collectionPath: "/A");
        vm.ActiveScope = "/A";
        vm.ActiveTab = vm.VisibleTabs[0]; // "a"

        vm.ActivatePreviousTab();
        vm.ActiveTab!.Name.Should().Be("b"); // wrapped to end
        vm.ActivatePreviousTab();
        vm.ActiveTab!.Name.Should().Be("a");
    }

    [Fact]
    public void ActivateNext_NoOp_WithSingleVisibleTab()
    {
        var vm = NewVm();
        vm.OpenOrActivate(Req("a"), "/A/a.req.json", collectionPath: "/A");
        vm.ActiveScope = "/A";
        var only = vm.ActiveTab;
        vm.ActivateNextTab();
        vm.ActiveTab.Should().BeSameAs(only);
    }

    [Fact]
    public void ActivateNext_OnlyCyclesVisibleScope()
    {
        var vm = NewVm();
        vm.OpenOrActivate(Req("a1"), "/A/a1.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("a2"), "/A/a2.req.json", collectionPath: "/A");
        vm.OpenOrActivate(Req("b1"), "/B/b1.req.json", collectionPath: "/B");
        vm.ActiveScope = "/A";
        vm.ActiveTab = vm.VisibleTabs[0];

        vm.ActivateNextTab();
        vm.ActiveTab!.Name.Should().Be("a2");
        vm.ActivateNextTab();
        vm.ActiveTab!.Name.Should().Be("a1"); // never lands on b1 (out of scope)
    }
}
