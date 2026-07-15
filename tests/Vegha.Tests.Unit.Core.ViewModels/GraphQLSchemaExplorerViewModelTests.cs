using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.Core.GraphQL.Schema;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class GraphQLSchemaExplorerViewModelTests
{
    private const string Fixture = """
    {
      "data": { "__schema": {
        "queryType": { "name": "Query" },
        "mutationType": { "name": "Mutation" },
        "types": [
          { "kind": "OBJECT", "name": "Query",
            "fields": [
              { "name": "user", "args": [ { "name": "id", "type": { "kind": "SCALAR", "name": "ID" } } ],
                "type": { "kind": "OBJECT", "name": "User" } }
            ] },
          { "kind": "OBJECT", "name": "Mutation",
            "fields": [ { "name": "createUser", "args": [], "type": { "kind": "OBJECT", "name": "User" } } ] },
          { "kind": "OBJECT", "name": "User", "description": "A person",
            "fields": [
              { "name": "id", "args": [], "type": { "kind": "SCALAR", "name": "ID" } },
              { "name": "bestFriend", "args": [], "type": { "kind": "OBJECT", "name": "User" } }
            ] },
          { "kind": "ENUM", "name": "Role", "enumValues": [ { "name": "ADMIN" } ] }
        ]
      } }
    }
    """;

    private static GraphQLSchemaExplorerViewModel Vm()
    {
        var vm = new GraphQLSchemaExplorerViewModel();
        vm.SetSchema(IntrospectionJsonReader.Parse(Fixture));
        return vm;
    }

    [Fact]
    public void RootPage_ShowsRootsAndAllTypes()
    {
        var vm = Vm();
        vm.Breadcrumb.Should().Be("Schema");
        vm.CanGoBack.Should().BeFalse();
        vm.Rows.Should().Contain(r => r.Title == "query: Query" && r.TypeLink == "Query");
        vm.Rows.Should().Contain(r => r.Title == "mutation: Mutation");
        vm.Rows.Should().Contain(r => r.Title == "User" && r.TypeLink == "User");
        vm.Rows.Should().Contain(r => r.Title == "Role" && r.Subtitle == "enum");
    }

    [Fact]
    public void NavigateAndBack_MaintainsStack()
    {
        var vm = Vm();
        vm.NavigateTo("Query");
        vm.Breadcrumb.Should().Be("Schema › Query");
        vm.Rows.Should().Contain(r => r.Title == "user(id: ID): User" && r.TypeLink == "User");

        vm.NavigateTo("User");
        vm.Breadcrumb.Should().Be("Schema › Query › User");
        vm.CanGoBack.Should().BeTrue();
        vm.Rows.Should().Contain(r => r.Title == "A person");

        vm.Back();
        vm.Breadcrumb.Should().Be("Schema › Query");
        vm.Back();
        vm.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void NavigateTo_UnknownOrNull_Ignored()
    {
        var vm = Vm();
        vm.NavigateTo(null);
        vm.NavigateTo("Nope");
        vm.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void SelfNavigation_DoesNotStackDuplicates()
    {
        var vm = Vm();
        vm.NavigateTo("User");
        vm.NavigateTo("User"); // clicking User.bestFriend: User while on User
        vm.Breadcrumb.Should().Be("Schema › User");
        vm.Back();
        vm.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Search_FindsTypesAndFields_Debounced()
    {
        var vm = Vm();
        vm.SearchText = "user";
        await Task.Delay(400); // > 150 ms debounce

        vm.Rows.Should().Contain(r => r.Title == "User" && r.TypeLink == "User");
        vm.Rows.Should().Contain(r => r.Title.StartsWith("Query.user(") && r.TypeLink == "Query");
        vm.Rows.Should().Contain(r => r.Title.StartsWith("Mutation.createUser"));
    }

    [Fact]
    public void SetSchema_ResetsNavigationAndSearch()
    {
        var vm = Vm();
        vm.NavigateTo("User");
        vm.SetSchema(IntrospectionJsonReader.Parse(Fixture));
        vm.Breadcrumb.Should().Be("Schema");
        vm.SearchText.Should().BeEmpty();
    }
}
