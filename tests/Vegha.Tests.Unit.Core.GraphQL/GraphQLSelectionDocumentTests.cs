using FluentAssertions;
using Vegha.Core.GraphQL;
using Vegha.Core.GraphQL.Builder;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class GraphQLSelectionDocumentTests
{
    [Fact]
    public void Parse_NestedFieldsAndArgs_VerbatimLiterals()
    {
        var result = GraphQLSelectionDocument.Parse("""
            query Continent {
              continent(code: "EU") {
                countries(first: 3, filter: { x: 1 }) {
                  name
                  capital
                }
              }
            }
            """);

        result.IsBuilderCompatible.Should().BeTrue();
        var op = result.Operations.Single();
        op.Kind.Should().Be(GraphQLOperationKind.Query);
        op.Name.Should().Be("Continent");

        var continent = op.Selections.Single();
        continent.Name.Should().Be("continent");
        continent.Args.Single().Should().Be(new KeyValuePair<string, string>("code", "\"EU\""));

        var countries = continent.Children.Single();
        countries.Args.Should().Equal(
            new KeyValuePair<string, string>("first", "3"),
            new KeyValuePair<string, string>("filter", "{ x: 1 }"));
        countries.Children.Select(c => c.Name).Should().Equal("name", "capital");
    }

    [Fact]
    public void Parse_InterpolationTokenArg_SurvivesVerbatim()
    {
        var result = GraphQLSelectionDocument.Parse(
            "query Q { user(id: {{userId}}) { name } }");
        result.IsBuilderCompatible.Should().BeTrue();
        result.Operations.Single().Selections.Single().Args.Single().Value
            .Should().Be("{{userId}}");
    }

    [Theory]
    [InlineData("query Q { a { ...F } } fragment F on T { x }", "fragments")]
    [InlineData("query Q { a { ... on T { x } } }", "inline fragments")]
    [InlineData("query Q { a @include(if: true) }", "directives")]
    [InlineData("query Q { alias: a }", "aliases")]
    public void Parse_UnsupportedConstructs_FlagIncompatible(string doc, string reason)
    {
        var result = GraphQLSelectionDocument.Parse(doc);
        result.IsBuilderCompatible.Should().BeFalse();
        result.IncompatibleReason.Should().Contain(reason);
    }

    [Fact]
    public void Parse_SyntaxError_ReportsTransientReason()
    {
        var result = GraphQLSelectionDocument.Parse("query { a {");
        result.IsBuilderCompatible.Should().BeFalse();
        result.IncompatibleReason.Should().Be("syntax error");
    }

    [Fact]
    public void Render_RoundTripsThroughParse()
    {
        var op = new SelectionOperation { Kind = GraphQLOperationKind.Query, Name = "Q" };
        var user = new SelectionNode { Name = "user" };
        user.Args.Add(new("id", "\"u1\""));
        user.Children.Add(new SelectionNode { Name = "name" });
        op.Selections.Add(user);

        var text = GraphQLSelectionDocument.Render(new[] { op });
        text.Should().Contain("query Q {");
        text.Should().Contain("user(id: \"u1\") {");

        var reparsed = GraphQLSelectionDocument.Parse(text);
        reparsed.IsBuilderCompatible.Should().BeTrue();
        reparsed.Operations.Single().Selections.Single().Children.Single().Name.Should().Be("name");
    }

    [Fact]
    public void Render_MultipleOperations_EmptyOnesSkipped()
    {
        var q = new SelectionOperation { Kind = GraphQLOperationKind.Query };
        q.Selections.Add(new SelectionNode { Name = "ping" });
        var m = new SelectionOperation { Kind = GraphQLOperationKind.Mutation }; // empty

        var text = GraphQLSelectionDocument.Render(new[] { q, m });
        text.Should().Contain("query {");
        text.Should().NotContain("mutation");
    }

    [Fact]
    public void Render_EmptyCompositeSelection_EmitsBraces()
    {
        var op = new SelectionOperation();
        op.Selections.Add(new SelectionNode { Name = "countries", ForceSelectionSet = true });
        GraphQLSelectionDocument.Render(new[] { op }).Should().Contain("countries {");
    }
}
