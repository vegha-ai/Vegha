using FluentAssertions;
using Vegha.Core.GraphQL.Schema;
using Vegha.Tests.Unit.Core.GraphQL.TestData;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class SdlRendererTests
{
    [Fact]
    public void RendersAllTypeKinds_RootsFirst()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        var sdl = SdlRenderer.Render(schema);

        sdl.IndexOf("type Query", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(sdl.IndexOf("type User", StringComparison.Ordinal),
                "root types render before alphabetical types");
        sdl.Should().Contain("type User implements Node {");
        sdl.Should().Contain("interface Node {");
        sdl.Should().Contain("union SearchResult = User");
        sdl.Should().Contain("enum Role {");
        sdl.Should().Contain("input CreateUserInput {");
        sdl.Should().Contain("scalar DateTime");
        sdl.Should().NotContain("scalar String", "built-in scalars are implicit in SDL");
        sdl.Should().Contain("directive @cached(ttl: Int = 60) on FIELD | QUERY");
    }

    [Fact]
    public void FieldShapes_ArgsDefaultsDeprecation()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        var sdl = SdlRenderer.Render(schema);

        sdl.Should().Contain("user(id: ID!): User");
        sdl.Should().Contain("search(term: String = \"*\"): [SearchResult]");
        sdl.Should().Contain("friends(first: Int): [User!]");
        sdl.Should().Contain("legacyName: String @deprecated(reason: \"Use email\")");
        sdl.Should().Contain("role: Role = MEMBER");
    }

    [Fact]
    public void RenderedSdl_IsParseableGraphQL()
    {
        var schema = IntrospectionJsonReader.Parse(IntrospectionFixtures.Small);
        var sdl = SdlRenderer.Render(schema);

        var act = () => GraphQLParser.Parser.Parse(sdl);
        act.Should().NotThrow("the rendered SDL must be valid GraphQL");
    }
}
