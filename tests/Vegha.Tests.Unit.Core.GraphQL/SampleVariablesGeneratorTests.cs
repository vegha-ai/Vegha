using FluentAssertions;
using Vegha.Core.GraphQL;
using Vegha.Core.GraphQL.Schema;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class SampleVariablesGeneratorTests
{
    private static IReadOnlyList<GraphQLVariableInfo> VarsOf(string operation) =>
        GraphQLDocumentAnalyzer.Analyze(operation).Operations.Single().Variables;

    [Fact]
    public void ScalarTypes_GetNeutralPlaceholders()
    {
        var json = SampleVariablesGenerator.Generate(VarsOf(
            "query Q($s: String!, $i: Int, $f: Float!, $b: Boolean, $id: ID!) { x }"));

        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;
        parsed.GetProperty("s").GetString().Should().Be("");
        parsed.GetProperty("i").GetInt32().Should().Be(0);
        parsed.GetProperty("f").GetDouble().Should().Be(0.0);
        parsed.GetProperty("b").GetBoolean().Should().BeFalse();
        parsed.GetProperty("id").GetString().Should().Be("");
    }

    [Fact]
    public void ListAndInputObject_Types()
    {
        var json = SampleVariablesGenerator.Generate(VarsOf(
            "query Q($tags: [String!]!, $input: CreateUserInput!) { x }"));

        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;
        parsed.GetProperty("tags").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        parsed.GetProperty("input").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public void NoVariables_ReturnsEmptyObject()
    {
        SampleVariablesGenerator.Generate(Array.Empty<GraphQLVariableInfo>())
            .Trim().Should().Be("{}");
    }

    [Fact]
    public void MergeMissing_PreservesExistingValues_AddsOnlyAbsent()
    {
        var vars = VarsOf("query Q($id: ID!, $limit: Int) { x }");
        var merged = SampleVariablesGenerator.MergeMissing("""{ "id": "u_42" }""", vars);

        merged.Should().NotBeNull();
        var parsed = System.Text.Json.JsonDocument.Parse(merged!).RootElement;
        parsed.GetProperty("id").GetString().Should().Be("u_42");
        parsed.GetProperty("limit").GetInt32().Should().Be(0);
    }

    [Fact]
    public void MergeMissing_NothingToAdd_ReturnsNull()
    {
        var vars = VarsOf("query Q($id: ID!) { x }");
        SampleVariablesGenerator.MergeMissing("""{ "id": "u_42" }""", vars).Should().BeNull();
    }

    [Fact]
    public void MergeMissing_UnparseableExisting_ReturnsNull()
    {
        var vars = VarsOf("query Q($id: ID!) { x }");
        SampleVariablesGenerator.MergeMissing("{ not json", vars).Should().BeNull();
    }
}
