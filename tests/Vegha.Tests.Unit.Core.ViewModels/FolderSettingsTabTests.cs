using System;
using System.IO;
using System.Linq;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Folder settings moved from a modal dialog into a workspace tab (opened by a single
/// click on the folder row). Guards the open-or-activate keying, the "is this node's settings
/// tab active" query the tree's click handler relies on, the save → folder.bru → rehydrate
/// path, and cleanup when the owning collection is removed.</summary>
public class FolderSettingsTabTests : IDisposable
{
    private readonly string _root;

    public FolderSettingsTabTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-fst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bruno.json"),
            """{"version":"1","name":"FST","type":"collection","ignore":["node_modules",".git"]}""");
        var users = Path.Combine(_root, "users");
        Directory.CreateDirectory(users);
        File.WriteAllText(Path.Combine(users, "folder.bru"),
            "meta {\n  name: users\n  type: folder\n  seq: 1\n}\n");
        File.WriteAllText(Path.Combine(users, "list.bru"),
            "meta {\n  name: list\n  type: http\n}\n\nget {\n  url: https://x.test/users\n}\n");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static RequestEditorViewModel NewEditor() => new(
        executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
        oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
        scriptHost: new Vegha.Core.Scripting.JintHost(),
        logger: NullLogger<RequestEditorViewModel>.Instance);

    private (CollectionsViewModel Vm, OpenTabsViewModel Tabs, CollectionFolderViewModel Folder) Setup()
    {
        var vm = new CollectionsViewModel(NewEditor(), NullLogger<CollectionsViewModel>.Instance);
        vm.LoadFromDirectory(_root);
        var tabs = new OpenTabsViewModel(NewEditor, NullLogger<OpenTabsViewModel>.Instance);
        var folder = (CollectionFolderViewModel)vm.Roots[0].Children.First(c => c.Name == "users");
        return (vm, tabs, folder);
    }

    [Fact]
    public void OpenSettingsTab_Twice_ActivatesExistingTab()
    {
        var (vm, tabs, folder) = Setup();
        var first = tabs.OpenSettingsTab(new FolderSettingsTabViewModel(vm, folder, _root));
        var second = tabs.OpenSettingsTab(new FolderSettingsTabViewModel(vm, folder, _root));

        second.Should().BeSameAs(first);
        tabs.Tabs.OfType<FolderSettingsTabViewModel>().Should().ContainSingle();
        tabs.IsResponsePaneApplicable.Should().BeFalse("settings tabs get the full vertical area");
        first.RequestCount.Should().Be(1);
        first.IsCollection.Should().BeFalse();
    }

    [Fact]
    public void IsSettingsTabActive_TracksActiveTab()
    {
        var (vm, tabs, folder) = Setup();
        tabs.IsSettingsTabActive(folder.Path).Should().BeFalse();

        tabs.OpenSettingsTab(new FolderSettingsTabViewModel(vm, folder, _root));
        tabs.IsSettingsTabActive(folder.Path).Should().BeTrue();
        tabs.IsSettingsTabActive(_root).Should().BeFalse("the collection's own settings tab isn't open");

        tabs.OpenSettingsTab(new CollectionSettingsTabViewModel(vm, (CollectionRootViewModel)vm.Roots[0]));
        tabs.IsSettingsTabActive(folder.Path).Should().BeFalse("another tab took focus");
        tabs.IsSettingsTabActive(_root).Should().BeTrue();
    }

    [Fact]
    public void Save_WritesFolderBru_AndRehydratesFromReloadedFolder()
    {
        var (vm, tabs, folder) = Setup();
        var tab = (FolderSettingsTabViewModel)tabs.OpenSettingsTab(new FolderSettingsTabViewModel(vm, folder, _root));
        var before = tab.Props;

        tab.Props.Headers[0].Name = "X-Team";
        tab.Props.Headers[0].Value = "core";
        tab.Props.SaveCommand.Execute(null);

        File.ReadAllText(Path.Combine(folder.Path, "folder.bru")).Should().Contain("X-Team");
        tab.Props.Should().NotBeSameAs(before, "the tab rehydrates from the reloaded folder");
        tab.Props.Headers.Should().Contain(h => h.Name == "X-Team" && h.Value == "core");
    }

    [Fact]
    public void CloseCollectionSettingsTab_AlsoClosesFolderTabsInThatCollection()
    {
        var (vm, tabs, folder) = Setup();
        tabs.OpenSettingsTab(new CollectionSettingsTabViewModel(vm, (CollectionRootViewModel)vm.Roots[0]));
        tabs.OpenSettingsTab(new FolderSettingsTabViewModel(vm, folder, _root));

        tabs.CloseCollectionSettingsTab(_root);

        tabs.Tabs.OfType<NodeSettingsTabViewModel>().Should().BeEmpty();
    }

    [Fact]
    public void OpenFolderSettingsCommand_RaisesTabRequest()
    {
        var (vm, _, folder) = Setup();
        CollectionFolderViewModel? requested = null;
        vm.FolderSettingsTabRequested += (_, f) => requested = f;

        vm.OpenFolderSettingsCommand.Execute(folder);

        requested.Should().BeSameAs(folder);
    }

    [Fact]
    public void SwitchingToCollectionWithNoTabs_OpensItsSettingsTab()
    {
        var other = Path.Combine(Path.GetTempPath(), "vegha-fst2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "bruno.json"),
            """{"version":"1","name":"Other","type":"collection"}""");
        try
        {
            var tabs = new OpenTabsViewModel(NewEditor, NullLogger<OpenTabsViewModel>.Instance);
            var vm = new CollectionsViewModel(NewEditor(), NullLogger<CollectionsViewModel>.Instance, tabs);
            vm.LoadFromDirectory(_root);
            vm.LoadFromDirectory(other);
            vm.CollectionSettingsTabRequested += (_, r) => tabs.OpenSettingsTab(new CollectionSettingsTabViewModel(vm, r));
            vm.ActiveCollection = vm.Roots.OfType<CollectionRootViewModel>().First();
            tabs.Tabs.Should().BeEmpty("auto-open stays off until the host has restored tabs");
            vm.OpenSettingsForEmptyCollection = true;
            var first = vm.Roots.OfType<CollectionRootViewModel>().First(r => string.Equals(r.SourcePath, _root, StringComparison.OrdinalIgnoreCase));
            var second = vm.Roots.OfType<CollectionRootViewModel>().First(r => string.Equals(r.SourcePath, other, StringComparison.OrdinalIgnoreCase));

            vm.ActiveCollection = second;
            vm.ActiveCollection = first;
            tabs.Tabs.OfType<CollectionSettingsTabViewModel>().Select(t => t.CollectionPath)
                .Should().Contain(first.SourcePath, "nothing was open in FST");

            // A collection that already has a tab keeps it — no settings tab is forced open.
            tabs.CloseCollectionSettingsTab(second.SourcePath);
            tabs.OpenOrActivate(new Vegha.Core.Domain.RequestItem { Name = "r", Kind = Vegha.Core.Domain.RequestKind.Http, Method = "GET", Url = "https://x.test" },
                Path.Combine(second.SourcePath, "r.bru"), collectionPath: second.SourcePath);
            vm.ActiveCollection = second;
            tabs.Tabs.OfType<CollectionSettingsTabViewModel>()
                .Should().NotContain(t => t.CollectionPath == second.SourcePath);
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { /* best-effort */ }
        }
    }
}
