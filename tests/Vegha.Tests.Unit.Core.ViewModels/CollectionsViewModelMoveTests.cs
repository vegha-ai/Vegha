using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>End-to-end tests for the drag-drop tree move. We materialise a real Bruno-style
/// folder layout in a temp dir, load it into the VM, then call <see cref="CollectionsViewModel.MoveNode"/>
/// directly (bypassing the UI drag-drop wiring) and verify both the in-memory tree and the
/// on-disk state.</summary>
public class CollectionsViewModelMoveTests : IDisposable
{
    private readonly string _root;

    public CollectionsViewModelMoveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // .. /collection.bru — minimum collection metadata Bruno's loader expects.
        File.WriteAllText(Path.Combine(_root, "bruno.json"), """
            {"version":"1","name":"Move-Test","type":"collection","ignore":["node_modules",".git"]}
            """);

        // root/folder-a/req-1.bru
        var folderA = Path.Combine(_root, "folder-a");
        Directory.CreateDirectory(folderA);
        File.WriteAllText(Path.Combine(folderA, "req-1.bru"), MakeBru("req-1", "GET", "https://a.test/1"));

        // root/folder-b/req-2.bru
        var folderB = Path.Combine(_root, "folder-b");
        Directory.CreateDirectory(folderB);
        File.WriteAllText(Path.Combine(folderB, "req-2.bru"), MakeBru("req-2", "POST", "https://b.test/2"));

        // root/folder-c/inner/req-3.bru — folder-c needs at least one request anywhere in
        // its subtree so CollectionLoader emits it (the loader prunes empty folders).
        var folderCInner = Path.Combine(_root, "folder-c", "inner");
        Directory.CreateDirectory(folderCInner);
        File.WriteAllText(Path.Combine(folderCInner, "req-3.bru"), MakeBru("req-3", "GET", "https://c.test/3"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string MakeBru(string name, string method, string url) => $$"""
        meta {
          name: {{name}}
          type: http
        }

        {{method.ToLowerInvariant()}} {
          url: {{url}}
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

    private static CollectionFolderViewModel Folder(CollectionsViewModel vm, string name) =>
        (CollectionFolderViewModel)vm.Roots[0].Children.First(c => c.Name == name);

    private static CollectionItemViewModel Request(CollectionFolderViewModel folder, string name) =>
        (CollectionItemViewModel)folder.Children.First(c => c.Name == name);

    [Fact]
    public void Fixture_LoadsExpectedFolders()
    {
        var collection = CollectionLoader.Load(_root);
        collection.Folders.Select(f => f.Name).Should().Contain(new[] { "folder-a", "folder-b" });
    }

    [Fact]
    public void Vm_PopulatesTreeChildren_FromLoadedCollection()
    {
        var vm = NewVm();
        vm.Roots.Should().ContainSingle();
        var names = vm.Roots[0].Children.Select(c => c.Name).ToArray();
        names.Should().Contain("folder-a");
        names.Should().Contain("folder-b");
    }

    [Fact]
    public void Move_RequestAcrossFolders_MovesFileOnDisk_AndUpdatesTree()
    {
        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        var dest = Folder(vm, "folder-b");

        var ok = vm.MoveNode(src, dest);

        ok.Should().BeTrue();
        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "folder-b", "req-1.bru")).Should().BeTrue();

        // Tree was reloaded — new VM nodes. folder-a is now empty on disk so the loader
        // prunes it; folder-b should now contain req-1.
        vm.Roots[0].Children.Select(c => c.Name).Should().NotContain("folder-a");
        Folder(vm, "folder-b").Children.Select(c => c.Name).Should().Contain("req-1");
    }

    [Fact]
    public void Move_FolderIntoAnotherFolder_MovesDirectoryOnDisk()
    {
        var vm = NewVm();
        var src = Folder(vm, "folder-a");
        var dest = Folder(vm, "folder-b");

        var ok = vm.MoveNode(src, dest);

        ok.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "folder-a")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "folder-b", "folder-a")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "folder-b", "folder-a", "req-1.bru")).Should().BeTrue();
    }

    [Fact]
    public void Move_FolderIntoItself_IsRejected()
    {
        var vm = NewVm();
        var src = Folder(vm, "folder-a");
        var ok = vm.MoveNode(src, src);
        ok.Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "folder-a")).Should().BeTrue();  // unchanged
    }

    [Fact]
    public void Move_FolderIntoOwnDescendant_IsRejected()
    {
        var vm = NewVm();
        var c = Folder(vm, "folder-c");
        var inner = (CollectionFolderViewModel)c.Children.First(x => x.Name == "inner");

        var ok = vm.MoveNode(c, inner);
        ok.Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "folder-c")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "folder-c", "inner")).Should().BeTrue();
    }

    [Fact]
    public void Move_OntoLeafTarget_IsRejected()
    {
        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        var leafTarget = Request(Folder(vm, "folder-b"), "req-2");

        var ok = vm.MoveNode(src, leafTarget);
        ok.Should().BeFalse();
        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeTrue();
    }

    [Fact]
    public void Move_SameParent_IsNoOp()
    {
        var vm = NewVm();
        var folderA = Folder(vm, "folder-a");
        var src = Request(folderA, "req-1");

        var ok = vm.MoveNode(src, folderA);
        ok.Should().BeFalse();
        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeTrue();
    }

    [Fact]
    public void Move_RequestIntoCollectionRoot_PromotesToTopLevel()
    {
        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        var root = vm.Roots[0];

        var ok = vm.MoveNode(src, root);

        ok.Should().BeTrue();
        File.Exists(Path.Combine(_root, "req-1.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeFalse();
    }

    [Fact]
    public void Copy_ThenPaste_DuplicatesRequestIntoTargetFolder()
    {
        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        var dest = Folder(vm, "folder-b");

        vm.CopyNodeCommand.Execute(src);
        vm.PasteNodeCommand.Execute(dest);

        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "folder-b", "req-1.bru")).Should().BeTrue();
    }

    [Fact]
    public void Paste_WithoutPriorCopy_StatusReportsEmptyClipboard()
    {
        var vm = NewVm();
        vm.PasteNodeCommand.Execute(Folder(vm, "folder-a"));
        vm.StatusMessage.Should().Contain("empty");
    }

    [Fact]
    public void Paste_WithCollidingName_AppendsCopySuffix()
    {
        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        // Pre-seed a file with the same name in the target.
        File.WriteAllText(Path.Combine(_root, "folder-b", "req-1.bru"),
            MakeBru("req-1", "DELETE", "https://collide.test/"));
        // Reload so the VM sees the seeded file.
        vm.LoadFromDirectory(_root);
        vm.Roots.RemoveAt(0);  // drop the original; LoadFromDirectory adds another root
        vm.LoadFromDirectory(_root);

        vm.CopyNodeCommand.Execute(Request(Folder(vm, "folder-a"), "req-1"));
        vm.PasteNodeCommand.Execute(Folder(vm, "folder-b"));

        // Both should now exist in folder-b: the original collision + a uniquely-named copy.
        Directory.GetFiles(Path.Combine(_root, "folder-b"), "req-1*.bru").Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public void Move_CollidingFileName_FailsWithoutLossOfSource()
    {
        // Pre-seed a collision so the move must refuse.
        var dest = Path.Combine(_root, "folder-b", "req-1.bru");
        File.WriteAllText(dest, MakeBru("req-1", "DELETE", "https://collision.test/"));

        var vm = NewVm();
        var src = Request(Folder(vm, "folder-a"), "req-1");
        var folderB = Folder(vm, "folder-b");

        var ok = vm.MoveNode(src, folderB);
        ok.Should().BeFalse();
        // Source still exists, destination still exists.
        File.Exists(Path.Combine(_root, "folder-a", "req-1.bru")).Should().BeTrue();
        File.Exists(dest).Should().BeTrue();
    }
}
