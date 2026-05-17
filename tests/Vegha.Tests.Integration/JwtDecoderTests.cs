using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Unit tests for the JWT decoder used by the OAuth2 panel's Decoded Payload preview.
/// No signature validation — these only exercise the base64url-decode + JSON-parse path.
/// Lives in the Integration test project because that's where Core.Requests is referenced.</summary>
public class JwtDecoderTests
{
    // A minimal, well-known JWT. Header={alg:none,typ:JWT}, Payload=standard sample claims.
    // Built once by hand with `dotnet-jwt --no-sign` so the test inputs are stable.
    private const string SampleJwt =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0." +
        "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiYXVkIjoibWljcm9nYXRld2F5IiwiaXNzIjoiaHR0cHM6Ly9pc3N1ZXIuZXhhbXBsZS5jb20iLCJjbGllbnRfaWQiOiJjbGllbnQtMTIzIiwiYXBwbGljYXRpb25fbmFtZSI6ImV1X2RpZ2l0YWxfYXBpX2h1YiIsImV4cCI6MTc3ODYxNDEwNCwiaWF0IjoxNTE2MjM5MDIyfQ." +
        "";

    [Fact]
    public void LooksLikeJwt_AcceptsThreeDottedSegments()
    {
        JwtDecoder.LooksLikeJwt(SampleJwt).Should().BeTrue();
        JwtDecoder.LooksLikeJwt("a.b.c").Should().BeTrue();
    }

    [Fact]
    public void LooksLikeJwt_RejectsNonJwtShapes()
    {
        JwtDecoder.LooksLikeJwt(null).Should().BeFalse();
        JwtDecoder.LooksLikeJwt("").Should().BeFalse();
        JwtDecoder.LooksLikeJwt("opaque-token").Should().BeFalse();
        JwtDecoder.LooksLikeJwt("two.dots").Should().BeFalse();
        JwtDecoder.LooksLikeJwt("four.dot.token.here").Should().BeFalse();
    }

    [Fact]
    public void PrettyPrintPayload_ProducesIndentedJson()
    {
        var pretty = JwtDecoder.PrettyPrintPayload(SampleJwt);
        pretty.Should().NotBeNullOrEmpty();
        pretty.Should().Contain("\"sub\"");
        pretty.Should().Contain("\"client_id\"");
        // Indented JSON has newlines + two-space indent.
        pretty.Should().Contain("\n");
    }

    [Fact]
    public void PrettyPrintPayload_OnNonJwt_ReturnsEmpty()
    {
        JwtDecoder.PrettyPrintPayload("opaque-token").Should().Be(string.Empty);
        JwtDecoder.PrettyPrintPayload("").Should().Be(string.Empty);
        JwtDecoder.PrettyPrintPayload(null).Should().Be(string.Empty);
    }

    [Fact]
    public void PrettyPrintPayload_OnMalformedPayloadSegment_ReturnsEmpty()
    {
        // Three dot-separated segments but the payload segment is invalid base64url.
        var bad = "aaa.!!!notbase64!!!.ccc";
        JwtDecoder.PrettyPrintPayload(bad).Should().Be(string.Empty);
    }

    [Fact]
    public void TryExtractClaims_ExtractsStandardClaimsFromSampleToken()
    {
        var claims = JwtDecoder.TryExtractClaims(SampleJwt);
        claims.Should().NotBeNull();
        claims!.Audience.Should().Be("microgateway");
        claims.Issuer.Should().Be("https://issuer.example.com");
        claims.Subject.Should().Be("1234567890");
        claims.ClientId.Should().Be("client-123");
        claims.ApplicationName.Should().Be("eu_digital_api_hub");
        claims.IssuedAt.Should().NotBeNull();
        claims.Expiry.Should().NotBeNull();
        // exp is far in the future in the sample (year 2026+).
        claims.Expiry!.Value.Year.Should().BeGreaterThanOrEqualTo(2026);
    }

    [Fact]
    public void TryExtractClaims_OnNonJwt_ReturnsNull()
    {
        JwtDecoder.TryExtractClaims("not-a-jwt").Should().BeNull();
    }

    [Fact]
    public void TryExtractClaims_HandlesAudienceAsArray()
    {
        // {"aud":["a","b","c"]} encoded with no padding.
        var payload = "eyJhdWQiOlsiYSIsImIiLCJjIl19";
        var token = "header." + payload + ".sig";
        var claims = JwtDecoder.TryExtractClaims(token);
        claims.Should().NotBeNull();
        claims!.Audience.Should().Be("a, b, c");
    }

    [Fact]
    public void TryDecodeHeaderJson_RecoversHeader()
    {
        var json = JwtDecoder.TryDecodeHeaderJson(SampleJwt);
        json.Should().NotBeNullOrEmpty();
        json!.Should().Contain("\"alg\"");
        json.Should().Contain("\"typ\"");
    }
}
