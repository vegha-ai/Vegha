using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Tests for the prompt-driven clone. Cloning now asks for the copy's name (seeded with
/// "&lt;name&gt; (copy)") and raises <c>RequestFileCloned</c> so the host can open the copy in a tab,
/// so these cover the name suggestion, the on-disk result under a caller-supplied name, and the
/// event that drives the tab.</summary>
public class CollectionsViewModelCloneTests : IDisposable
{
    private readonly string _root;

    public CollectionsViewModelCloneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bruno.json"), """
            {"version":"1","name":"Clone-Test","type":"collection","ignore":["node_modules",".git"]}
            """);
        var folder = Path.Combine(_root, "folder-a");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "CCB_AddEmail.bru"), MakeBru("CCB_AddEmail"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string MakeBru(string name) => $$"""
        meta {
          name: {{name}}
          type: http
        }

        post {
          url: https://apir-stage.example.com/ccb/ATC1Person
        }
        """;

    private CollectionsViewModel NewVm()
    {
        var editor = new RequestEditorViewModel(
            executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);
        var vm = new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);
        vm.LoadFromDirectory(_root);
        return vm;
    }

    private static CollectionFolderViewModel FolderA(CollectionsViewModel vm) =>
        (CollectionFolderViewModel)vm.Roots[0].Children.First(c => c.Name == "folder-a");

    private static CollectionItemViewModel Request(CollectionsViewModel vm, string name) =>
        (CollectionItemViewModel)FolderA(vm).Children.First(c => c.Name == name);

    [Fact]
    public void SuggestCloneName_AppendsCopySuffix()
    {
        var vm = NewVm();
        vm.SuggestCloneName(Request(vm, "CCB_AddEmail")).Should().Be("CCB_AddEmail (copy)");
    }

    [Fact]
    public void SuggestCloneName_SkipsPastAnExistingCopy()
    {
        File.WriteAllText(Path.Combine(_root, "folder-a", "CCB_AddEmail (copy).bru"), MakeBru("CCB_AddEmail (copy)"));
        var vm = NewVm();

        vm.SuggestCloneName(Request(vm, "CCB_AddEmail")).Should().Be("CCB_AddEmail (copy) 2",
            "the prompt should open on a name that won't collide");
    }

    [Fact]
    public void CloneNodeAs_WritesFileAndMetaNameUnderTheChosenName()
    {
        var vm = NewVm();

        var dest = vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "CCB_DeleteEmail");

        dest.Should().NotBeNull();
        File.Exists(dest!).Should().BeTrue();
        Path.GetFileName(dest).Should().Be("CCB_DeleteEmail.bru");
        File.ReadAllText(dest!).Should().Contain("name: CCB_DeleteEmail",
            "the clone's meta.name drives the tree label, so it must match the chosen name");
    }

    [Fact]
    public void CloneNodeAs_LeavesTheOriginalUntouched()
    {
        var vm = NewVm();
        var original = Path.Combine(_root, "folder-a", "CCB_AddEmail.bru");

        vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "CCB_DeleteEmail");

        File.Exists(original).Should().BeTrue();
        File.ReadAllText(original).Should().Contain("name: CCB_AddEmail");
    }

    [Fact]
    public void CloneNodeAs_RaisesRequestFileCloned_SoTheHostCanOpenTheTab()
    {
        var vm = NewVm();
        string? raised = null;
        vm.RequestFileCloned += (_, p) => raised = p;

        var dest = vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "CCB_DeleteEmail");

        raised.Should().Be(dest, "the host opens the cloned request from this event");
    }

    [Fact]
    public void CloneNodeAs_UniquifiesWhenTheTypedNameIsTaken()
    {
        var vm = NewVm();

        var dest = vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "CCB_AddEmail");

        Path.GetFileName(dest).Should().Be("CCB_AddEmail 2.bru",
            "a typed name that collides must not overwrite the original");
        File.ReadAllText(Path.Combine(_root, "folder-a", "CCB_AddEmail.bru"))
            .Should().Contain("name: CCB_AddEmail");
    }

    [Fact]
    public void CloneNodeAs_SanitizesPathSeparatorsOutOfTypedNames()
    {
        var vm = NewVm();

        var dest = vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "bad/name");

        dest.Should().NotBeNull();
        Path.GetDirectoryName(dest).Should().Be(Path.Combine(_root, "folder-a"),
            "a typed slash must not escape the containing folder");
    }

    [Fact]
    public void CloneNodeAs_IgnoresABlankName()
    {
        var vm = NewVm();
        vm.CloneNodeAs(Request(vm, "CCB_AddEmail"), "   ").Should().BeNull();
    }
}
