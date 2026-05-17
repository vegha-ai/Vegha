using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class ProxyCredentialProtectorTests
{
    [Fact]
    public void Protect_EmptyOrNull_ReturnsEmpty()
    {
        ProxyCredentialProtector.Protect(null).Should().BeEmpty();
        ProxyCredentialProtector.Protect("").Should().BeEmpty();
    }

    [Fact]
    public void Protect_PrefixesWithSchemeTag()
    {
        var got = ProxyCredentialProtector.Protect("hunter2");
        got.Should().StartWith("v1:b64:");
    }

    [Fact]
    public void Unprotect_RoundTripsArbitraryUnicode()
    {
        var inputs = new[]
        {
            "hunter2",
            "p@ss w0rd!",
            "🔑 emoji 한글",   // multi-byte UTF-8
            new string('x', 1024), // long
        };

        foreach (var input in inputs)
        {
            var ciphertext = ProxyCredentialProtector.Protect(input);
            var back = ProxyCredentialProtector.Unprotect(ciphertext);
            back.Should().Be(input);
        }
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnsAsIs()
    {
        // Settings written by builds before this helper existed stored the password raw.
        // The unprotect path must not destroy those values.
        var legacy = "myplaintextpassword";
        ProxyCredentialProtector.Unprotect(legacy).Should().Be(legacy);
    }

    [Fact]
    public void Unprotect_GarbledV1Payload_ReturnsEmpty()
    {
        // Truncated / corrupted base64 — must not throw and must not return junk.
        ProxyCredentialProtector.Unprotect("v1:b64:not-real-base64!!").Should().BeEmpty();
    }

    [Fact]
    public void Unprotect_UnknownScheme_ReturnsEmpty()
    {
        // A future version writing "v2:..." that we don't understand: prefer empty
        // over silently returning a value we can't decode correctly.
        ProxyCredentialProtector.Unprotect("v1:future-scheme:xyz").Should().BeEmpty();
    }
}
