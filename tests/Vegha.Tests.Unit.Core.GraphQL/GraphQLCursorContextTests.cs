using FluentAssertions;
using Vegha.Core.GraphQL.Editor;
using Vegha.Core.GraphQL.Schema;
using Vegha.Tests.Unit.Core.GraphQL.TestData;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

/// <summary>Table-driven cursor-context tests. The caret position is marked with <c>┃</c>
/// in each case's document text.</summary>
public class GraphQLCursorContextTests
{
    private static readonly GraphQLSchemaModel Schema =
        IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);

    private static GraphQLCursorContext At(string docWithCaret)
    {
        var caret = docWithCaret.IndexOf('┃');
        caret.Should().BeGreaterOrEqualTo(0, "test doc must contain a ┃ caret marker");
        var doc = docWithCaret.Remove(caret, 1);
        return GraphQLCursorContextEngine.Compute(doc, caret, Schema);
    }

    [Theory]
    [InlineData("┃")]
    [InlineData("query Q { a } ┃")]
    public void TopLevel_IsOperationKeyword(string doc) =>
        At(doc).Kind.Should().Be(GraphQLCompletionContextKind.OperationKeyword);

    [Fact]
    public void RootSelection_Query()
    {
        var ctx = At("query Q { ┃ }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().Be("Query");
    }

    [Fact]
    public void RootSelection_Shorthand()
    {
        var ctx = At("{ ┃ }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().Be("Query");
    }

    [Fact]
    public void RootSelection_Mutation()
    {
        var ctx = At("mutation M { ┃ }");
        ctx.ContainerTypeName.Should().Be("Mutation");
    }

    [Fact]
    public void NestedSelection_UsesFieldReturnType()
    {
        var ctx = At("query Q { user(id: \"1\") { ┃ } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().Be("User");
    }

    [Fact]
    public void DeepNesting_ListTypeUnwraps()
    {
        var ctx = At("query Q { user(id: \"1\") { friends { ┃ } } }");
        ctx.ContainerTypeName.Should().Be("User", "friends: [User!] unwraps to User");
    }

    [Fact]
    public void PoppedFrame_ReturnsToParentType()
    {
        var ctx = At("query Q { user(id: \"1\") { id } ┃ }");
        ctx.ContainerTypeName.Should().Be("Query");
    }

    [Fact]
    public void PartialWord_TrackedWithReplaceStart()
    {
        var ctx = At("query Q { user(id: \"1\") { ema┃ } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.PartialWord.Should().Be("ema");
    }

    [Fact]
    public void ArgumentName_InsideParens()
    {
        var ctx = At("query Q { user(┃) { id } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.ArgumentName);
        ctx.ContainerTypeName.Should().Be("Query");
        ctx.FieldName.Should().Be("user");
    }

    [Fact]
    public void SecondArgument_AfterComma()
    {
        var ctx = At("query Q { search(term: \"x\", ┃) }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.ArgumentName);
        ctx.FieldName.Should().Be("search");
    }

    [Fact]
    public void ArgumentValue_AfterColon()
    {
        var ctx = At("query Q($r: Role) { user(id: ┃) { id } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.ArgumentValue);
        ctx.ArgumentName.Should().Be("id");
        ctx.DeclaredVariables.Should().Contain("r");
    }

    [Fact]
    public void VariableUse_AfterDollar_InValue()
    {
        var ctx = At("query Q($id: ID!) { user(id: $┃) { id } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.VariableUse);
        ctx.DeclaredVariables.Should().Contain("id");
    }

    [Fact]
    public void VariableDefinitionType_AfterColonInHeader()
    {
        var ctx = At("query Q($id: ┃) { user(id: $id) { id } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.VariableDefinitionType);
    }

    [Fact]
    public void FragmentCondition_AfterOn_InInlineFragment()
    {
        var ctx = At("query Q { search(term: \"x\") { ... on ┃ } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FragmentConditionType);
    }

    [Fact]
    public void FragmentCondition_AfterOn_InFragmentHeader()
    {
        var ctx = At("fragment F on ┃");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FragmentConditionType);
    }

    [Fact]
    public void InlineFragment_PushesConditionType()
    {
        var ctx = At("query Q { search(term: \"x\") { ... on User { ┃ } } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().Be("User");
    }

    [Fact]
    public void FragmentDefinitionBody_UsesConditionType()
    {
        var ctx = At("fragment F on User { ┃ }");
        ctx.ContainerTypeName.Should().Be("User");
    }

    [Fact]
    public void Directive_AfterAt()
    {
        var ctx = At("query Q { user(id: \"1\") @┃ }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.Directive);
    }

    [Fact]
    public void InsideString_NoCompletion()
    {
        var ctx = At("query Q { user(id: \"u┃ }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.None);
    }

    [Fact]
    public void InputObjectLiteral_DoesNotCorruptSelectionStack()
    {
        var ctx = At("mutation M { createUser(input: {email: \"a\"}) { ┃ } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().Be("User");
    }

    [Fact]
    public void InterpolationToken_DoesNotBreakContext()
    {
        var ctx = At("query Q { user(id: {{userId}}) { ┃ } }");
        ctx.ContainerTypeName.Should().Be("User");
    }

    [Fact]
    public void UnknownField_ChildFrame_FailsOpen_NullType()
    {
        var ctx = At("query Q { nonsense { ┃ } }");
        ctx.Kind.Should().Be(GraphQLCompletionContextKind.FieldSelection);
        ctx.ContainerTypeName.Should().BeNull();
    }

    [Fact]
    public void SecondOperation_ResetsDeclaredVariables()
    {
        var ctx = At("query A($a: ID!) { user(id: $a) { id } } query B($b: Int) { search(term: ┃) }");
        ctx.DeclaredVariables.Should().Equal("b");
    }

    [Fact]
    public void CaretAtEveryOffset_NeverThrows()
    {
        const string doc = """
            # comment "quote {{brace
            query Q($id: ID!, $opts: CreateUserInput = {email: "x"}) @cached(ttl: 60) {
              user(id: $id) {
                ... on Admin { role }
                ...Fields
                friends(first: 3) { id }
              }
            }
            fragment Fields on User { name(x: [1, 2]) "broken
            """;
        for (var caret = 0; caret <= doc.Length; caret++)
        {
            var act = () => GraphQLCursorContextEngine.Compute(doc, caret, Schema);
            act.Should().NotThrow($"caret at {caret} must not throw");
        }
    }
}
