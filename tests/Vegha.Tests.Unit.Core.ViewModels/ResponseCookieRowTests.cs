using Vegha.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class ResponseCookieRowTests
{
    [Fact]
    public void Parse_PullsAllStandardAttributes()
    {
        var row = ResponseCookieRow.Parse(
            "session=abc123; Path=/; Domain=example.test; Expires=Wed, 01 Jan 2025 00:00:00 GMT; "
            + "Max-Age=3600; Secure; HttpOnly; SameSite=Strict",
            fallbackDomain: "fallback.test");

        row.Should().NotBeNull();
        row!.Name.Should().Be("session");
        row.Value.Should().Be("abc123");
        row.Path.Should().Be("/");
        row.Domain.Should().Be("example.test");
        row.MaxAge.Should().Be(3600);
        row.Secure.Should().BeTrue();
        row.HttpOnly.Should().BeTrue();
        row.SameSite.Should().Be("Strict");
        row.Expires.Should().NotBeNull();
    }

    [Fact]
    public void Parse_FallsBackToHostDomain_WhenAttributeAbsent()
    {
        var row = ResponseCookieRow.Parse("token=xyz; Path=/", "api.example.test");
        row!.Domain.Should().Be("api.example.test");
    }

    [Fact]
    public void Parse_NoFlags_FlagsLabelIsEmpty()
    {
        var row = ResponseCookieRow.Parse("k=v", "example.test");
        row!.FlagsLabel.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AllFlags_FlagsLabelLists_Them()
    {
        var row = ResponseCookieRow.Parse("k=v; Secure; HttpOnly; SameSite=Lax", "example.test");
        row!.FlagsLabel.Should().Contain("Secure");
        row.FlagsLabel.Should().Contain("HttpOnly");
        row.FlagsLabel.Should().Contain("SameSite=Lax");
    }

    [Theory]
    [InlineData("")]
    [InlineData("nokeyvalue")]
    [InlineData("=novalue")]
    public void Parse_Malformed_ReturnsNull(string raw)
    {
        ResponseCookieRow.Parse(raw, "example.test").Should().BeNull();
    }
}
