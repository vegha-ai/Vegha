using System.Net.Http;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>
/// Regression tests for request-name drift. The name used to live in five hand-synced copies —
/// the editor's private loaded item, the tab strip label, meta.name in the .bru, the file stem,
/// and the tree node — so a rename that missed one of them got silently reverted by the next
/// save. These tests pin the invariant that replaced that: the editor owns the name, the tab
/// delegates to it, and every save emits from that one cell.
/// </summary>
public class RequestNameOwnershipTests
{
    private static RequestEditorViewModel CreateVm()
    {
        var http = new HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    private static RequestItem NamedRequest(string name, string url = "https://example.com/ATC1Person") =>
        new() { Name = name, Method = "POST", Url = url };

    [Fact]
    public void Rename_ThenBuildItem_EmitsTheNewName()
    {
        // The original bug: rename updated the file + tab, the editor kept the old name, and the
        // next save wrote the old name back into meta.name.
        var vm = CreateVm();
        vm.LoadFromRequestItem(NamedRequest("ATC1Person"), "/c/ATC1Person.bru");

        vm.ApplyRename("CCB_DeleteEmail");

        vm.BuildRequestItemFromVm().Name.Should().Be("CCB_DeleteEmail",
            "the save path must emit the renamed value, not the name the VM was loaded with");
    }

    [Fact]
    public void Rename_ThenEditAndRebuild_KeepsTheNewName()
    {
        // The reported repro: rename, then change something and run. The edit must not resurrect
        // the loaded name.
        var vm = CreateVm();
        vm.LoadFromRequestItem(NamedRequest("ATC1Person"), "/c/ATC1Person.bru");
        vm.ApplyRename("CCB_DeleteEmail");

        vm.Url = "https://example.com/ATC1Person?changed=1";
        vm.Headers.Add(new KvEntry("X-Test", "1", true));

        vm.BuildRequestItemFromVm().Name.Should().Be("CCB_DeleteEmail",
            "editing a renamed request must never revert its name");
    }

    [Fact]
    public void TabLabel_FollowsEditorName_WithoutExplicitSync()
    {
        var vm = CreateVm();
        var item = NamedRequest("ATC1Person");
        vm.LoadFromRequestItem(item, "/c/ATC1Person.bru");
        var tab = new HttpRequestTabViewModel(vm, item, "/c/ATC1Person.bru", "/c/ATC1Person.bru");

        vm.ApplyRename("CCB_DeleteEmail");

        tab.Name.Should().Be("CCB_DeleteEmail",
            "the tab label reads the editor's cell, so it can't fall out of step");
    }

    [Fact]
    public void RenamingViaTab_ReachesTheSavedItem()
    {
        // The other direction: the tab strip writes through to the same cell the save reads.
        var vm = CreateVm();
        var item = NamedRequest("ATC1Person");
        vm.LoadFromRequestItem(item, "/c/ATC1Person.bru");
        var tab = new HttpRequestTabViewModel(vm, item, "/c/ATC1Person.bru", "/c/ATC1Person.bru");

        tab.Name = "CCB_AddEmail";

        vm.BuildRequestItemFromVm().Name.Should().Be("CCB_AddEmail");
    }

    [Fact]
    public void UrlEdit_DoesNotRenameANamedRequest()
    {
        // Drafts mirror the URL into the label; a named request must be immune, or typing in the
        // URL bar silently renames the user's request.
        var vm = CreateVm();
        var item = NamedRequest("CCB_DeleteEmail");
        vm.LoadFromRequestItem(item, "/c/CCB_DeleteEmail.bru");
        var tab = new HttpRequestTabViewModel(vm, item, "/c/CCB_DeleteEmail.bru", "/c/CCB_DeleteEmail.bru");

        vm.Url = "https://example.com/ccb_BGECISWebservices/ATC1Person";

        tab.Name.Should().Be("CCB_DeleteEmail");
    }

    [Fact]
    public void UrlEdit_StillLabelsAnUnnamedDraft()
    {
        // The mirror must keep working for genuine drafts — that's the affordance it exists for.
        var vm = CreateVm();
        var tab = new HttpRequestTabViewModel(vm, request: null, sourcePath: null, id: "draft-1");
        tab.Name.Should().Be("Untitled");

        vm.Url = "https://example.com/orders";

        tab.Name.Should().Contain("example.com");
    }

    [Fact]
    public void PastedCurl_DoesNotRenameANamedRequest()
    {
        // A curl's name is derived from the URL's last path segment. Pasting one replaces the
        // request's content, not its identity.
        var vm = CreateVm();
        vm.LoadFromRequestItem(NamedRequest("CCB_DeleteEmail"), "/c/CCB_DeleteEmail.bru");

        vm.Url = "curl -X POST https://apir-stage.exeloncorp.com/ccb_BGECISWebservices/ATC1Person";

        vm.RequestName.Should().Be("CCB_DeleteEmail");
        vm.BuildRequestItemFromVm().Name.Should().Be("CCB_DeleteEmail",
            "a pasted curl must not overwrite a name the user chose");
    }

    [Fact]
    public void PastedCurl_NamesAnUnnamedDraft()
    {
        var vm = CreateVm();

        vm.Url = "curl -X POST https://apir-stage.exeloncorp.com/ccb_BGECISWebservices/ATC1Person";

        vm.RequestName.Should().Be("ATC1Person",
            "an unnamed draft should still adopt the curl's derived name");
    }

    [Fact]
    public void PlaceholderName_DoesNotCountAsDeliberate()
    {
        // "Untitled" is seeded chrome, not a choice — latching it would freeze a draft's label.
        var vm = CreateVm();
        vm.LoadFromRequestItem(new RequestItem { Name = "Untitled", Url = "" }, sourcePath: null);

        vm.HasExplicitName.Should().BeFalse();
    }
}
