using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class ExportDialogViewModelTests
{
    private static Collection Api(string name) => new()
    {
        Name = name,
        Requests = { new RequestItem { Name = "Ping", Method = "GET", Url = "https://x.test/ping" } },
        Folders = { new Folder { Name = "Streams", Requests = { new RequestItem { Name = "Watch", Kind = RequestKind.Grpc } } } },
    };

    // Paths that don't exist on disk → the VM falls back to the in-memory model.
    private static (string, string, Collection?)[] Choices() => new (string, string, Collection?)[]
    {
        ("Alpha", @"Z:\nope\alpha", Api("Alpha")),
        ("Beta", @"Z:\nope\beta", Api("Beta")),
    };

    [Fact]
    public void File_menu_defaults_to_workspace_scope_with_everything_checked()
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(Choices(), "Team", @"Z:\ws");
        vm.IsWorkspaceScope.Should().BeTrue();
        vm.Collections.Should().OnlyContain(c => c.IsSelected);
        vm.CanExport.Should().BeTrue();
    }

    [Fact]
    public void Right_click_preselects_only_that_collection()
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(Choices(), "Team", @"Z:\ws", preselectPath: @"z:\NOPE\beta");
        vm.IsSelectionScope.Should().BeTrue();
        vm.Collections.Where(c => c.IsSelected).Select(c => c.Name).Should().Equal("Beta");
    }

    [Fact]
    public void Workspace_scope_is_unavailable_without_a_workspace()
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(Choices(), null, null);
        vm.HasWorkspace.Should().BeFalse();
        vm.IsWorkspaceScope.Should().BeFalse();
    }

    [Fact]
    public void CanExport_tracks_the_checklist_in_selection_scope()
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(Choices(), "Team", @"Z:\ws", preselectPath: @"Z:\nope\alpha");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Collections[0].IsSelected = false;
        vm.CanExport.Should().BeFalse();
        raised.Should().Contain(nameof(ExportDialogViewModel.CanExport));

        vm.IsSelectionScope = false; // workspace scope ignores the checklist
        vm.CanExport.Should().BeTrue();
    }

    [Fact]
    public void OpenApi_hides_and_disables_environment_export()
    {
        var vm = new ExportDialogViewModel { SelectedFormat = ExportFormatInfo.For(ExportFormat.OpenApi30) };
        vm.IsOpenApi.Should().BeTrue();
        vm.SupportsEnvironments.Should().BeFalse();
        vm.BuildOptions().IncludeEnvironments.Should().BeFalse();
    }

    [Fact]
    public async Task Builds_and_saves_selected_collections_with_warnings()
    {
        var vm = new ExportDialogViewModel { SelectedFormat = ExportFormatInfo.For(ExportFormat.InsomniaV4) };
        vm.Initialize(Choices(), null, null, preselectPath: @"Z:\nope\alpha");

        var result = await vm.BuildAsync();
        result.Should().NotBeNull();
        result!.SuggestedFileName.Should().Be("Alpha.insomnia.json");

        using var ms = new MemoryStream();
        (await vm.SaveAsync(result, ms, "out.json")).Should().BeTrue();
        ms.ToArray().Should().Equal(result.Content);
        vm.StatusMessage.Should().Be("Exported 1 collection to out.json.");
        vm.HasWarnings.Should().BeTrue("the gRPC request can't be represented");
    }

    [Fact]
    public async Task Multiple_selected_collections_export_as_a_zip()
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(Choices(), null, null);
        var result = await vm.BuildAsync();
        result!.IsZip.Should().BeTrue();
        result.CollectionCount.Should().Be(2);
    }
}
