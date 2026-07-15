using FluentAssertions;
using Vegha.Core.GraphQL;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

public class InterpolationMaskerTests
{
    [Fact]
    public void MaskPreservesLength_Always()
    {
        const string text = "query { user(id: {{userId}}, t: \"{{host}}\") { id } }";
        var masked = InterpolationMasker.Mask(text);
        masked.Length.Should().Be(text.Length);
        masked.Should().NotContain("{{");
    }

    [Fact]
    public void MaskedTokenIsIdentifierLike()
    {
        var masked = InterpolationMasker.Mask("{{tenantId}}");
        masked.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
        masked.Length.Should().Be("{{tenantId}}".Length);
    }

    [Fact]
    public void NoTokens_SameInstanceReturned()
    {
        const string text = "query { user { id } }";
        InterpolationMasker.Mask(text).Should().BeSameAs(text);
    }

    [Fact]
    public void StructuralDoubleBraces_AreNotMasked()
    {
        // "}}" closers of nested selection sets and "{ {" like sequences must survive.
        const string text = "query{user{id}}";
        InterpolationMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void UnterminatedToken_LeftAlone()
    {
        const string text = "query { user(id: {{userId } }";
        InterpolationMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void MultipleTokens_AllMasked()
    {
        var masked = InterpolationMasker.Mask("{{a}} {{bb}} x {{ccc}}");
        masked.Should().NotContain("{{");
        masked.Length.Should().Be("{{a}} {{bb}} x {{ccc}}".Length);
    }
}
