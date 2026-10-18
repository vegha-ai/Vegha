using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Rename / recolor from the Edit environment dialog must only touch metadata —
/// variable rows the user hasn't saved yet stay in the editor.</summary>
public class EnvironmentsViewModelEditDetailsTests : IDisposable
{
    private readonly string _root;

    public EnvironmentsViewModelEditDetailsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-envedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bruno.json"),
            """{"version":"1","name":"EnvEdit","type":"collection"}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CollectionsViewModel _collections = null!;

    private EnvironmentsViewModel NewVm()
    {
        var editor = new RequestEditorViewModel(
            executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);
        var collections = _collections = new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);
        collections.LoadFromDirectory(_root);
        collections.ActiveCollection = collections.Roots.OfType<CollectionRootViewModel>().First();

        var store = new Vegha.Core.Persistence.WorkspaceStore(
            Path.Combine(_root, "ws", "workspaces.json"));
        var workspaces = new WorkspacesViewModel(store, collections, NullLogger<WorkspacesViewModel>.Instance);

        return new EnvironmentsViewModel(collections, workspaces,
            new Vegha.Integrations.Secrets.SecretRegistry(),
            NullLogger<EnvironmentsViewModel>.Instance);
    }

    private static void TypeUnsavedVariable(EnvironmentsViewModel vm)
    {
        var ghost = vm.Variables.Last();
        ghost.Name = "baseUrl";
        ghost.Value = "https://example.test";
    }

    [Fact]
    public void CreateEnvironment_AssignsAColor()
    {
        var vm = NewVm();
        vm.CreateEnvironmentCommand.Execute(null);

        vm.SelectedEnvironment!.Color.Should().MatchRegex("^#[0-9A-F]{6}$");
    }

    [Fact]
    public async Task UpdateDetails_Rename_KeepsUnsavedVariables()
    {
        var vm = NewVm();
        vm.CreateEnvironmentCommand.Execute(null);
        TypeUnsavedVariable(vm);

        await vm.UpdateDetailsAsync(vm.SelectedEnvironment!, "staging", vm.SelectedEnvironment!.Color);

        vm.SelectedEnvironment!.Name.Should().Be("staging");
        vm.Variables.Should().Contain(r => r.Name == "baseUrl" && r.Value == "https://example.test");
        vm.Variables.Last().IsBlank.Should().BeTrue("the ghost row is still at the tail");
    }

    [Fact]
    public async Task UpdateDetails_RenameAndRecolor_KeepsUnsavedVariables_AndSaveIncludesThem()
    {
        var vm = NewVm();
        vm.CreateEnvironmentCommand.Execute(null);
        TypeUnsavedVariable(vm);

        await vm.UpdateDetailsAsync(vm.SelectedEnvironment!, "prod", "#123456");

        vm.SelectedEnvironment!.Name.Should().Be("prod");
        vm.SelectedEnvironment!.Color.Should().Be("#123456");
        vm.Variables.Should().Contain(r => r.Name == "baseUrl");

        await vm.SaveCommand.ExecuteAsync(null);
        vm.SelectedEnvironment!.Variables.Should().Contain(v => v.Name == "baseUrl");
    }

    [Fact]
    public async Task UpdateDetails_Recolor_ThenWatcherReload_KeepsUnsavedVariables()
    {
        var vm = NewVm();
        EmulateListBox(vm);
        vm.CreateEnvironmentCommand.Execute(null);
        TypeUnsavedVariable(vm);

        await vm.UpdateDetailsAsync(vm.SelectedEnvironment!, vm.SelectedEnvironment!.Name, "#51BE1B");
        await WatcherReloadAsync();

        vm.SelectedEnvironment!.Color.Should().Be("#51BE1B");
        vm.Variables.Should().Contain(r => r.Name == "baseUrl" && r.Value == "https://example.test");
        vm.IsDirty.Should().BeTrue();
    }

    /// <summary>Mimics the master ListBox's two-way SelectedItem: when the selected item is
    /// swapped out (Replace) or the list resets, the ListBox pushes null back into the VM.</summary>
    private static void EmulateListBox(EnvironmentsViewModel vm)
    {
        vm.Filtered.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                || (e.OldItems?.Contains(vm.SelectedEnvironment) ?? false))
                vm.SelectedEnvironment = null;
        };
    }

    /// <summary>Runs the same reload the collection's file watcher triggers after a write.</summary>
    private async Task WatcherReloadAsync()
    {
        var root = _collections.Roots.OfType<CollectionRootViewModel>().First();
        var m = typeof(CollectionsViewModel).GetMethod("ReloadRootContainingAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)m.Invoke(_collections, new object[] { root })!;
    }

    [Fact]
    public async Task Recolor_Reload_Save_Reload_ShowsSavedVariables()
    {
        var vm = NewVm();
        EmulateListBox(vm);
        vm.CreateEnvironmentCommand.Execute(null);
        await WatcherReloadAsync();
        TypeUnsavedVariable(vm);

        await vm.UpdateDetailsAsync(vm.SelectedEnvironment!, vm.SelectedEnvironment!.Name, "#51BE1B");
        await WatcherReloadAsync();
        vm.Variables.Should().Contain(r => r.Name == "baseUrl", "draft survives the recolor reload");

        await vm.SaveCommand.ExecuteAsync(null);
        vm.Variables.Should().Contain(r => r.Name == "baseUrl", "right after Save");
        await WatcherReloadAsync();

        vm.SelectedEnvironment.Should().NotBeNull();
        vm.SelectedEnvironment!.Variables.Should().Contain(v => v.Name == "baseUrl");
        vm.Variables.Should().Contain(r => r.Name == "baseUrl", "after the save reload");
    }

    [Fact]
    public async Task Save_ThenWatcherReload_ShowsSavedVariables()
    {
        var vm = NewVm();
        EmulateListBox(vm);
        vm.CreateEnvironmentCommand.Execute(null);
        TypeUnsavedVariable(vm);

        await vm.SaveCommand.ExecuteAsync(null);
        vm.Variables.Should().Contain(r => r.Name == "baseUrl");
        vm.IsDirty.Should().BeFalse();

        await WatcherReloadAsync();
        vm.Variables.Should().Contain(r => r.Name == "baseUrl" && r.Value == "https://example.test");
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void SwitchingToAnotherEnvironment_DiscardsDraft()
    {
        var vm = NewVm();
        vm.CreateEnvironmentCommand.Execute(null);
        var first = vm.SelectedEnvironment!;
        vm.CreateEnvironmentCommand.Execute(null);
        var second = vm.SelectedEnvironment!;

        vm.SelectedEnvironment = first;
        TypeUnsavedVariable(vm);
        vm.SelectedEnvironment = second;
        vm.SelectedEnvironment = first;

        vm.Variables.Should().NotContain(r => r.Name == "baseUrl");
    }

    [Fact]
    public void Reset_DiscardsDraft()
    {
        var vm = NewVm();
        vm.CreateEnvironmentCommand.Execute(null);
        TypeUnsavedVariable(vm);

        vm.ResetCommand.Execute(null);

        vm.Variables.Should().NotContain(r => r.Name == "baseUrl");
        vm.IsDirty.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, "#D92626")]
    [InlineData(120, "#26D926")]
    [InlineData(240, "#2626D9")]
    public void HslToHex_ProducesExpectedColors(double hue, string expected)
    {
        EnvironmentsViewModel.HslToHex(hue, 0.70, 0.50).Should().Be(expected);
    }
}
