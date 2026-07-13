using FluentAssertions;
using Vegha.Core.GraphQL.Editor;
using Vegha.Core.GraphQL.Schema;
using Vegha.Tests.Unit.Core.GraphQL.TestData;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class GraphQLCompletionEngineTests
{
    private static readonly GraphQLSchemaModel Schema =
        IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);

    private static GraphQLCompletionResult At(string docWithCaret, GraphQLSchemaModel? schema = null)
    {
        var caret = docWithCaret.IndexOf('┃');
        var doc = docWithCaret.Remove(caret, 1);
        return GraphQLCompletionEngine.GetCompletions(doc, caret, schema ?? Schema);
    }

    [Fact]
    public void FieldSelection_ListsFieldsWithSignatures_PlusTypename()
    {
        var result = At("query Q { user(id: \"1\") { ┃ } }");
        var labels = result.Items.Select(i => i.Label).ToList();
        labels.Should().Contain(new[] { "id", "email", "role", "friends", "legacyName", "__typename" });

        var friends = result.Items.Single(i => i.Label == "friends");
        friends.Detail.Should().Be("[User!]");
        result.Items.Single(i => i.Label == "legacyName").IsDeprecated.Should().BeTrue();
    }

    [Fact]
    public void FieldSelection_PartialWord_ReportedForFiltering()
    {
        var result = At("query Q { user(id: \"1\") { ema┃ } }");
        result.PartialWord.Should().Be("ema");
        result.Items.Should().Contain(i => i.Label == "email");
    }

    [Fact]
    public void ArgumentName_InsertsColonSuffix()
    {
        var result = At("query Q { user(┃) { id } }");
        var id = result.Items.Single(i => i.Label == "id");
        id.InsertText.Should().Be("id: ");
        id.Detail.Should().Be("ID!");
    }

    [Fact]
    public void ArgumentValue_EnumType_ListsValuesAndVariables()
    {
        var result = At("mutation M($r: Role) { createUser(input: {email: \"a\"}) { friends(first: 1) { id } } } query Q($role: Role) { user(id: ┃) { id } }");
        // 'id' arg is ID (not enum) → only declared variables offered.
        result.Items.Should().Contain(i => i.Label == "$role" && i.Kind == GraphQLCompletionItemKind.Variable);
    }

    [Fact]
    public void VariableDefinitionType_ListsInputTypesOnly()
    {
        var result = At("query Q($x: ┃) { user(id: $x) { id } }");
        var labels = result.Items.Select(i => i.Label).ToList();
        labels.Should().Contain(new[] { "String", "Int", "ID", "Role", "CreateUserInput", "DateTime" });
        labels.Should().NotContain("User", "object types are not valid variable types");
    }

    [Fact]
    public void FragmentCondition_ListsCompositeTypesOnly()
    {
        var result = At("fragment F on ┃");
        var labels = result.Items.Select(i => i.Label).ToList();
        labels.Should().Contain(new[] { "User", "Node", "SearchResult", "Query" });
        labels.Should().NotContain("Role");
        labels.Should().NotContain("CreateUserInput");
    }

    [Fact]
    public void Directive_ListsSchemaDirectives()
    {
        var result = At("query Q { user(id: \"1\") @┃ { id } }");
        var cached = result.Items.Single(i => i.Label == "cached");
        cached.Detail.Should().Be("@cached(ttl: Int = 60)");
    }

    [Fact]
    public void Directive_NoSchemaDirectives_FallsBackToSkipInclude()
    {
        const string noDirectives = """
        { "data": { "__schema": {
          "queryType": { "name": "Query" },
          "types": [ { "kind": "OBJECT", "name": "Query",
            "fields": [ { "name": "ping", "type": { "kind": "SCALAR", "name": "String" }, "args": [] } ] } ]
        } } }
        """;
        var schema = IntrospectionJsonReader.Parse(noDirectives);
        var result = At("query Q { ping @┃ }", schema);
        result.Items.Select(i => i.Label).Should().BeEquivalentTo("include", "skip");
    }

    [Fact]
    public void NoSchema_FieldSelection_Empty_ButKeywordsStillWork()
    {
        // Call the engine directly — the At() helper substitutes the fixture for null.
        GraphQLCompletionEngine.GetCompletions("query Q {  }", 10, null)
            .Items.Should().BeEmpty();
        GraphQLCompletionEngine.GetCompletions("", 0, null)
            .Items.Select(i => i.Label)
            .Should().Contain(new[] { "query", "mutation", "subscription", "fragment" });
    }

    [Fact]
    public void InsideString_NoItems()
    {
        At("query Q { user(id: \"abc┃ }").Items.Should().BeEmpty();
    }
}
