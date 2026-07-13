using FluentAssertions;
using Vegha.Core.GraphQL;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class GraphQLFormatterTests
{
    [Fact]
    public void MinifiedQuery_IsExpanded()
    {
        var pretty = GraphQLFormatter.Prettify("query GetUser($id:ID!){user(id:$id){id email friends{name}}}");

        pretty.Should().Contain("query GetUser($id: ID!)");
        pretty.Split('\n').Length.Should().BeGreaterThan(3, "prettify should expand to multiple lines");
        // Idempotent: prettifying the output again changes nothing.
        GraphQLFormatter.Prettify(pretty).Should().Be(pretty);
    }

    [Fact]
    public void InvalidDocument_ReturnedUnchanged()
    {
        const string broken = "query { user { id ";
        GraphQLFormatter.Prettify(broken).Should().Be(broken);
    }

    [Fact]
    public void DocumentWithInterpolationToken_ReturnedUnchanged()
    {
        // {{var}} as a value doesn't lex as GraphQL — Prettify must not corrupt it.
        const string doc = "query { user(tenant: {{tenantId}}) { id } }";
        GraphQLFormatter.Prettify(doc).Should().Be(doc);
    }

    [Fact]
    public void SemanticContent_SurvivesRoundTrip()
    {
        const string doc = """
            mutation M($input: CreateUserInput!) {
              createUser(input: $input) {
                id
                profile { avatarUrl }
              }
            }
            """;
        var pretty = GraphQLFormatter.Prettify(doc);
        var reAnalyzed = GraphQLDocumentAnalyzer.Analyze(pretty);
        reAnalyzed.SyntaxErrors.Should().BeEmpty();
        reAnalyzed.Operations.Should().ContainSingle();
        reAnalyzed.Operations[0].Name.Should().Be("M");
        reAnalyzed.Operations[0].Variables.Should().ContainSingle()
            .Which.Should().Be(new GraphQLVariableInfo("input", "CreateUserInput!", false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyInput_ReturnsEmpty(string? text)
    {
        GraphQLFormatter.Prettify(text).Should().Be(text ?? string.Empty);
    }
}
