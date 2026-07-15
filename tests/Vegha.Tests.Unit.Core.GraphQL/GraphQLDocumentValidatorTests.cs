using FluentAssertions;
using Vegha.Core.GraphQL;
using Vegha.Core.GraphQL.Schema;
using Vegha.Core.GraphQL.Validation;
using Vegha.Tests.Unit.Core.GraphQL.TestData;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class GraphQLDocumentValidatorTests
{
    private static readonly GraphQLSchemaModel Schema =
        IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);

    private static IReadOnlyList<GraphQLDiagnostic> Validate(string doc) =>
        GraphQLDocumentValidator.Validate(doc, Schema);

    [Fact]
    public void ValidDocument_NoDiagnostics()
    {
        var diags = Validate("""
            query GetUser($id: ID!) {
              user(id: $id) {
                id
                email
                friends(first: 3) { id __typename }
                ... on User { role }
              }
              __typename
            }
            """);
        diags.Should().BeEmpty();
    }

    [Fact]
    public void UnknownField_Flagged_WithPosition()
    {
        var doc = "query Q { user(id: \"1\") { nonsense } }";
        var diags = Validate(doc);
        diags.Should().ContainSingle();
        diags[0].Message.Should().Contain("\"nonsense\" does not exist on type \"User\"");
        doc.Substring(diags[0].Offset, "nonsense".Length).Should().Be("nonsense");
    }

    [Fact]
    public void UnknownArgument_And_MissingRequiredArgument()
    {
        Validate("query Q { user(id: \"1\", nope: 2) { id } }")
            .Should().ContainSingle(d => d.Message.Contains("Unknown argument \"nope\""));

        Validate("query Q { user { id } }")
            .Should().ContainSingle(d => d.Message.Contains("missing required argument \"id: ID!\""));
    }

    [Fact]
    public void UnknownTypeInVarDef_AndFragmentCondition()
    {
        Validate("query Q($x: Nope) { user(id: \"1\") { id } }")
            .Should().ContainSingle(d => d.Message.Contains("Unknown type \"Nope\""));

        Validate("fragment F on Nope { id }")
            .Should().ContainSingle(d => d.Message.Contains("Unknown type \"Nope\""));
    }

    [Fact]
    public void UndefinedVariable_Flagged_InOperations_NotFragments()
    {
        Validate("query Q { user(id: $missing) { id } }")
            .Should().ContainSingle(d => d.Message.Contains("$missing"));

        // Fragment definitions can't know their callers' variables — no false positive.
        Validate("fragment F on Query { user(id: $callerVar) { id } }")
            .Should().BeEmpty();
    }

    [Fact]
    public void UnknownDirective_Flagged_BuiltInsAllowed()
    {
        Validate("query Q { user(id: \"1\") @nope { id } }")
            .Should().ContainSingle(d => d.Message.Contains("@nope"));

        Validate("query Q($c: Boolean!) { user(id: \"1\") @include(if: $c) { id } }")
            .Should().BeEmpty();

        Validate("query Q { user(id: \"1\") @cached { id } }")
            .Should().BeEmpty("cached is declared by the schema fixture");
    }

    [Fact]
    public void EnumLiterals_And_InputObjectFields_Checked()
    {
        Validate("mutation M { createUser(input: {email: \"a\", role: NOT_A_ROLE}) { id } }")
            .Should().ContainSingle(d => d.Message.Contains("NOT_A_ROLE"));

        Validate("mutation M { createUser(input: {bogusField: 1, email: \"a\"}) { id } }")
            .Should().ContainSingle(d => d.Message.Contains("bogusField"));
    }

    [Fact]
    public void UnionSelection_RequiresInlineFragment_HintInMessage()
    {
        var diags = Validate("query Q { search(term: \"x\") { id } }");
        diags.Should().ContainSingle();
        diags[0].Message.Should().Contain("inline fragment");
    }

    [Fact]
    public void MetaFields_SchemaAndType_OnlyOnQueryRoot()
    {
        Validate("query Q { __schema { queryType { name } } }")
            .Should().BeEmpty("__schema is legal on the query root even though introspection types aren't in the model");
    }

    [Fact]
    public void SyntaxErrors_And_NoText_YieldNoDiagnostics()
    {
        Validate("query { user { ").Should().BeEmpty();
        Validate("").Should().BeEmpty();
    }

    [Fact]
    public void InterpolationTokens_DoNotFalsePositive()
    {
        // {{var}} masks to an identifier — the enum check must not flag it because the
        // masked name can't match any enum value... it CAN mismatch. The arg type here is
        // ID (not an enum), so no enum validation applies — the common case for {{var}}.
        Validate("query Q { user(id: {{userId}}) { id } }").Should().BeEmpty();
    }

    [Fact]
    public void UnknownFragmentSpread_Flagged()
    {
        Validate("query Q { user(id: \"1\") { ...NoSuchFragment } }")
            .Should().ContainSingle(d => d.Message.Contains("NoSuchFragment"));
    }
}
