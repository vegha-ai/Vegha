using FluentAssertions;
using Vegha.Core.GraphQL;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class GraphQLDocumentAnalyzerTests
{
    [Fact]
    public void SingleAnonymousQuery_OneOperation_NoName()
    {
        var info = GraphQLDocumentAnalyzer.Analyze("{ user { id } }");

        info.SyntaxErrors.Should().BeEmpty();
        info.Operations.Should().ContainSingle();
        info.Operations[0].Name.Should().BeNull();
        info.Operations[0].Kind.Should().Be(GraphQLOperationKind.Query);
    }

    [Fact]
    public void MultipleNamedOperations_AllReported_InDocumentOrder()
    {
        const string doc = """
            query GetUser($id: ID!) { user(id: $id) { id email } }
            mutation UpdateUser($id: ID!, $name: String) { updateUser(id: $id, name: $name) { id } }
            subscription OnUserChanged { userChanged { id } }
            """;

        var info = GraphQLDocumentAnalyzer.Analyze(doc);

        info.SyntaxErrors.Should().BeEmpty();
        info.Operations.Should().HaveCount(3);
        info.Operations[0].Should().Match<GraphQLOperationInfo>(o =>
            o.Name == "GetUser" && o.Kind == GraphQLOperationKind.Query);
        info.Operations[1].Should().Match<GraphQLOperationInfo>(o =>
            o.Name == "UpdateUser" && o.Kind == GraphQLOperationKind.Mutation);
        info.Operations[2].Should().Match<GraphQLOperationInfo>(o =>
            o.Name == "OnUserChanged" && o.Kind == GraphQLOperationKind.Subscription);
    }

    [Fact]
    public void VariableDefinitions_NamesTypesDefaults_Extracted()
    {
        var info = GraphQLDocumentAnalyzer.Analyze(
            "query Q($id: ID!, $tags: [String!], $limit: Int = 10) { node(id: $id) { id } }");

        info.Operations.Should().ContainSingle();
        var vars = info.Operations[0].Variables;
        vars.Should().HaveCount(3);
        vars[0].Should().Be(new GraphQLVariableInfo("id", "ID!", false));
        vars[1].Should().Be(new GraphQLVariableInfo("tags", "[String!]", false));
        vars[2].Should().Be(new GraphQLVariableInfo("limit", "Int", true));
    }

    [Fact]
    public void FragmentsAreNotOperations()
    {
        var info = GraphQLDocumentAnalyzer.Analyze("""
            query Q { user { ...UserFields } }
            fragment UserFields on User { id email }
            """);

        info.SyntaxErrors.Should().BeEmpty();
        info.Operations.Should().ContainSingle();
    }

    [Fact]
    public void SyntaxError_ReportedWithPosition_NotThrown()
    {
        var info = GraphQLDocumentAnalyzer.Analyze("query Q { user { id }"); // missing }

        info.Operations.Should().BeEmpty();
        info.SyntaxErrors.Should().ContainSingle();
        info.SyntaxErrors[0].Line.Should().BeGreaterOrEqualTo(1);
        info.SyntaxErrors[0].Offset.Should().BeInRange(0, 21);
        info.SyntaxErrors[0].Length.Should().BeGreaterOrEqualTo(1);
        info.SyntaxErrors[0].Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void InterpolationTokens_DoNotBreakParsing()
    {
        var info = GraphQLDocumentAnalyzer.Analyze("""
            query Q($id: ID!) {
              user(id: $id, tenant: {{tenantId}}) { id }
            }
            """);

        info.SyntaxErrors.Should().BeEmpty();
        info.Operations.Should().ContainSingle();
        info.Operations[0].Name.Should().Be("Q");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    public void EmptyInput_ReturnsEmpty(string? text)
    {
        var info = GraphQLDocumentAnalyzer.Analyze(text);
        info.Operations.Should().BeEmpty();
        info.SyntaxErrors.Should().BeEmpty();
    }

    [Fact]
    public void TruncatedInput_NeverThrows_AtAnyOffset()
    {
        const string doc = """
            # comment with {{var}} and "quote
            query GetUser($id: ID!, $opts: [OptsInput] = [{a: 1}]) @cached(ttl: 60) {
              user(id: $id) {
                ... on Admin { role }
                ...Fields
                friends(first: 3) { edges { node { id } } }
              }
            }
            fragment Fields on User { name "block
            """ + "\"\"\"docstring\"\"\"";

        for (var cut = 0; cut <= doc.Length; cut++)
        {
            var slice = doc[..cut];
            var act = () => GraphQLDocumentAnalyzer.Analyze(slice);
            act.Should().NotThrow($"input truncated at {cut} must not throw");
        }
    }
}
