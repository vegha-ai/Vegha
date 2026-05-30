using System;
using System.IO;
using System.Linq;
using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>An empty (but marker-bearing) folder surfaces a synthetic "+ New request" row so it
/// still shows an expand chevron and offers a one-click way to add its first request. The row is
/// rebuilt with the tree, so it's absent once the folder has real content, and it's suppressed
/// while a filter is active.</summary>
public class CollectionsViewModelPlaceholderTests : IDisposable
{
    private readonly string _root;

    public CollectionsViewModelPlaceholderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-ph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bruno.json"),
            """{"version":"1","name":"PH-Test","type":"collection","ignore":["node_modules",".git"]}""");

        // empty/ — folder.bru marker but no requests → the loader keeps it; the tree shows it empty.
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "folder.bru"),
            "meta {\n  name: empty\n  type: folder\n  seq: 1\n}\n");

        // filled/req.bru — a folder with real content gets no placeholder.
        var filled = Path.Combine(_root, "filled");
        Directory.CreateDirectory(filled);
        File.WriteAllText(Path.Combine(filled, "req.bru"),
            "meta {\n  name: req\n  type: http\n}\n\nget {\n  url: https://x.test/\n}\n");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

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

    [Fact]
    public void EmptyFolder_GetsSingleNewRequestPlaceholder()
    {
        var vm = NewVm();
        var empty = Folder(vm, "empty");

        empty.Children.Should().ContainSingle()
            .Which.Should().BeOfType<NewRequestPlaceholderViewModel>();
        ((NewRequestPlaceholderViewModel)empty.Children[0]).Parent.Should().BeSameAs(empty);
    }

    [Fact]
    public void FilledFolder_HasNoPlaceholder()
    {
        var vm = NewVm();
        Folder(vm, "filled").Children.OfType<NewRequestPlaceholderViewModel>().Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task Placeholder_IsHidden_WhileFilterActive()
    {
        var vm = NewVm();
        var placeholder = Folder(vm, "empty").Children.OfType<NewRequestPlaceholderViewModel>().Single();

        // A non-empty filter is debounced (~250ms) when a SynchronizationContext is present, as
        // it is under the test runner — poll rather than asserting synchronously.
        vm.Filter = "req";
        await WaitUntil(() => !placeholder.IsVisibleByFilter, TimeSpan.FromSeconds(2));
        placeholder.IsVisibleByFilter.Should().BeFalse("the create affordance is suppressed during search");

        vm.Filter = "";   // clearing the filter applies inline
        placeholder.IsVisibleByFilter.Should().BeTrue("with no filter the affordance is shown again");
    }

    private static async System.Threading.Tasks.Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(25);
    }
}
