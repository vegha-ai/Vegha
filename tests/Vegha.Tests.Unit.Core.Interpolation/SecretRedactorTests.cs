using Vegha.Core.Interpolation;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Interpolation;

public class SecretRedactorTests
{
    [Fact]
    public void Redact_ReplacesSecretValueWithMask()
    {
        var result = SecretRedactor.Redact(
            "curl --header 'Authorization: Bearer tok-abcdef'",
            new[] { "tok-abcdef" });

        result.Should().Be("curl --header 'Authorization: Bearer •••'");
    }

    [Fact]
    public void Redact_MasksEveryOccurrence()
    {
        var result = SecretRedactor.Redact("aaaa and aaaa again", new[] { "aaaa" });
        result.Should().Be("••• and ••• again");
    }

    [Fact]
    public void Redact_SkipsShortValues()
    {
        // A 3-char value would pepper unrelated text with masks — left untouched.
        var result = SecretRedactor.Redact("the cat sat", new[] { "cat" });
        result.Should().Be("the cat sat");
    }

    [Fact]
    public void Redact_MasksLongerValueFirst_SoOverlapsResolveCleanly()
    {
        // "secret-token" contains "secret" — masking the longer one first avoids a
        // partial "•••-token" outcome.
        var result = SecretRedactor.Redact("value=secret-token", new[] { "secret", "secret-token" });
        result.Should().Be("value=•••");
    }

    [Fact]
    public void Redact_NoSecrets_ReturnsTextUnchanged()
    {
        SecretRedactor.Redact("nothing to hide", System.Array.Empty<string>())
            .Should().Be("nothing to hide");
    }

    [Fact]
    public void Redact_EmptyText_ReturnsEmpty()
    {
        SecretRedactor.Redact(string.Empty, new[] { "whatever" }).Should().BeEmpty();
    }
}
