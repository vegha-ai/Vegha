using FluentAssertions;
using Vegha.Core.GraphQL;
using Vegha.Core.GraphQL.Schema;
using Vegha.Tests.Unit.Core.GraphQL.TestData;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class IntrospectionJsonReaderTests
{
    [Fact]
    public void SmallFixture_RootsAndTypes_Parsed()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);

        schema.QueryTypeName.Should().Be("Query");
        schema.MutationTypeName.Should().Be("Mutation");
        schema.SubscriptionTypeName.Should().Be("Subscription");
        schema.Types.Keys.Should().Contain(new[]
            { "Query", "User", "Node", "SearchResult", "Role", "CreateUserInput", "DateTime" });
        schema.Types.Keys.Should().NotContain("__Ignored", "meta types are skipped");
    }

    [Fact]
    public void TypeRefTrees_UnwrapCorrectly()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        var user = schema.FindType("User")!;

        var friends = user.Fields.Single(f => f.Name == "friends");
        friends.Type.Display.Should().Be("[User!]");
        friends.Type.UnwrappedName.Should().Be("User");

        var id = user.Fields.Single(f => f.Name == "id");
        id.Type.Display.Should().Be("ID!");
    }

    [Fact]
    public void ArgsDeprecationEnumsInputsInterfaces_AllParsed()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);

        var query = schema.FindType("Query")!;
        var userField = query.Fields.Single(f => f.Name == "user");
        userField.Args.Should().ContainSingle().Which.Should().Match<GraphQLArgInfo>(a =>
            a.Name == "id" && a.Type.Display == "ID!" && a.Description == "User id");

        var search = query.Fields.Single(f => f.Name == "search");
        search.Args[0].DefaultValue.Should().Be("\"*\"");

        var user = schema.FindType("User")!;
        user.Fields.Single(f => f.Name == "legacyName").IsDeprecated.Should().BeTrue();
        user.Interfaces.Should().Equal("Node");

        schema.FindType("Role")!.EnumValues.Select(v => v.Name).Should().Equal("ADMIN", "MEMBER");
        schema.FindType("CreateUserInput")!.InputFields.Should().HaveCount(2);
        schema.FindType("Node")!.PossibleTypes.Should().Equal("User");
        schema.FindType("SearchResult")!.PossibleTypes.Should().Equal("User");
    }

    [Fact]
    public void Directives_Parsed()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        var cached = schema.Directives.Single(d => d.Name == "cached");
        cached.Locations.Should().Equal("FIELD", "QUERY");
        cached.Args.Single().DefaultValue.Should().Be("60");
    }

    [Fact]
    public void RootTypeFor_MapsOperationKinds()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        schema.RootTypeFor(GraphQLOperationKind.Query)!.Name.Should().Be("Query");
        schema.RootTypeFor(GraphQLOperationKind.Mutation)!.Name.Should().Be("Mutation");
        schema.RootTypeFor(GraphQLOperationKind.Subscription)!.Name.Should().Be("Subscription");
    }

    [Fact]
    public void ErrorsArray_ThrowsServerRejected_BeforeLookingAtData()
    {
        var act = () => IntrospectionJsonReader.Parse(IntrospectionFixtures.IntrospectionDisabled);
        act.Should().Throw<GraphQLIntrospectionException>()
            .Which.ServerRejected.Should().BeTrue();
    }

    [Fact]
    public void MissingSchema_Throws_NotServerRejected()
    {
        var act = () => IntrospectionJsonReader.Parse("{\"data\":{}}");
        act.Should().Throw<GraphQLIntrospectionException>()
            .Which.ServerRejected.Should().BeFalse();
    }

    [Fact]
    public void MinimalQueryShape_ParsesWithEmptySections()
    {
        // The abridged fallback query returns no inputFields/interfaces/directives — the
        // reader must not require them.
        const string minimal = """
        {
          "data": { "__schema": {
            "queryType": { "name": "Query" },
            "types": [
              { "kind": "OBJECT", "name": "Query",
                "fields": [ { "name": "ping", "type": { "kind": "SCALAR", "name": "String" }, "args": [] } ] }
            ]
          } }
        }
        """;
        var schema = IntrospectionJsonReader.Parse(minimal);
        schema.FindType("Query")!.Fields.Should().ContainSingle();
        schema.Directives.Should().BeEmpty();
        schema.MutationTypeName.Should().BeNull();
    }
}
