using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Filter tests for the revamped environments panel. The other VM commands
/// (Import/Export/Copy/Delete/SetColor) touch the filesystem and a real workspace and
/// are best covered by integration tests; the search filter is pure VM state and worth
/// pinning here.</summary>
public class EnvironmentsViewModelFilterTests
{
    private static EnvironmentsViewModel NewVm(out CollectionsViewModel collections)
    {
        var editor = new RequestEditorViewModel(
            executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);
        collections = new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);

        var store = new Vegha.Core.Persistence.WorkspaceStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "vegha-env-tests-" + System.Guid.NewGuid().ToString("N"), "workspaces.json"));
        var workspaces = new WorkspacesViewModel(store, collections, NullLogger<WorkspacesViewModel>.Instance);

        return new EnvironmentsViewModel(collections, workspaces,
            new Vegha.Integrations.Secrets.SecretRegistry(),
            NullLogger<EnvironmentsViewModel>.Instance);
    }

    [Fact]
    public void Filtered_Mirrors_All_When_SearchText_Empty()
    {
        var vm = NewVm(out var collections);
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Local" });
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Prod" });

        vm.Filtered.Should().HaveCount(2);
        vm.Filtered.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Local", "Prod" });
    }

    [Fact]
    public void Filtered_Updates_When_SearchText_Changes()
    {
        var vm = NewVm(out var collections);
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Local" });
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Prod" });
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Staging" });

        vm.SearchText = "pr";
        vm.Filtered.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Prod" });

        vm.SearchText = "g";
        vm.Filtered.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Staging" });

        vm.SearchText = "";
        vm.Filtered.Should().HaveCount(3);
    }

    [Fact]
    public void Filtered_Reacts_To_Add_And_Remove_Of_Source_List()
    {
        var vm = NewVm(out var collections);
        var first = new DomainEnv { Name = "Local" };
        collections.CollectionEnvironments.Add(first);
        vm.Filtered.Should().HaveCount(1);

        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Prod" });
        vm.Filtered.Should().HaveCount(2);

        collections.CollectionEnvironments.Remove(first);
        vm.Filtered.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Prod" });
    }

    [Fact]
    public void SearchText_Is_CaseInsensitive()
    {
        var vm = NewVm(out var collections);
        collections.CollectionEnvironments.Add(new DomainEnv { Name = "Production" });
        vm.SearchText = "PROD";
        vm.Filtered.Should().HaveCount(1);
    }
}
