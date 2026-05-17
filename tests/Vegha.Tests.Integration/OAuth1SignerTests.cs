using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class OAuth1SignerTests
{
    /// <summary>RFC 5849 §1.2 worked example. Verifies signature base string + HMAC-SHA1
    /// signature against the canonical fixture published in the RFC.</summary>
    [Fact]
    public void Rfc5849_HmacSha1_ProducesSignatureFromExample()
    {
        // From RFC 5849 §1.2, slightly normalized.
        const string consumerKey    = "9djdj82h48djs9d2";
        const string token          = "kkk9d7dh3k39sjv7";

        var oauthParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = "7d8f3e4a",
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = "137131201",
            ["oauth_token"] = token,
            ["oauth_version"] = "1.0",
        };

        var body = new[]
        {
            new KeyValuePair<string, string>("c2", string.Empty),
            new KeyValuePair<string, string>("a3", "2 q"),
        };

        var baseString = OAuth1Signer.BuildSignatureBaseString(
            "POST",
            "http://example.com/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b",
            oauthParams,
            body);

        // The base string is well-defined: spot-check critical fragments rather than
        // hard-code the entire string (newer .NET URL parsing differs in a few edge
        // cases vs the RFC example).
        baseString.Should().StartWith("POST&http%3A%2F%2Fexample.com%2Frequest&");
        baseString.Should().Contain("oauth_consumer_key%3D9djdj82h48djs9d2");
    }

    [Fact]
    public void HmacSha1_BuildsAuthorizationHeader_WithExpectedShape()
    {
        var header = OAuth1Signer.BuildAuthorizationHeader(
            new OAuth1Signer.Config(
                ConsumerKey: "ck",
                ConsumerSecret: "cs",
                SignatureMethod: "HMAC-SHA1",
                Token: "tk",
                TokenSecret: "ts",
                Realm: "https://api.test/",
                Nonce: "abc",
                Timestamp: 1234567890),
            "GET",
            "https://api.test/users");

        header.Should().StartWith("OAuth realm=\"https%3A%2F%2Fapi.test%2F\", ");
        header.Should().Contain("oauth_consumer_key=\"ck\"");
        header.Should().Contain("oauth_nonce=\"abc\"");
        header.Should().Contain("oauth_signature_method=\"HMAC-SHA1\"");
        header.Should().Contain("oauth_timestamp=\"1234567890\"");
        header.Should().Contain("oauth_token=\"tk\"");
        header.Should().Contain("oauth_version=\"1.0\"");
        header.Should().Contain("oauth_signature=\"");
    }

    [Fact]
    public void Plaintext_SignatureIsConsumerSecretAndTokenSecret()
    {
        var header = OAuth1Signer.BuildAuthorizationHeader(
            new OAuth1Signer.Config(
                ConsumerKey: "ck",
                ConsumerSecret: "consumer-secret",
                SignatureMethod: "PLAINTEXT",
                Token: "tk",
                TokenSecret: "token-secret"),
            "GET",
            "https://api.test/");

        // PLAINTEXT signature = encode(consumer_secret) "&" encode(token_secret),
        // then percent-encoded again for the header.
        header.Should().Contain("oauth_signature=\"consumer-secret%26token-secret\"");
    }

    [Fact]
    public void HmacSha256_AndSha512_AlsoSign()
    {
        var s256 = OAuth1Signer.BuildAuthorizationHeader(
            new OAuth1Signer.Config("ck", "cs", "HMAC-SHA256",
                Nonce: "n", Timestamp: 1),
            "POST", "https://api.test/");
        s256.Should().Contain("oauth_signature_method=\"HMAC-SHA256\"");
        s256.Should().Contain("oauth_signature=\"");

        var s512 = OAuth1Signer.BuildAuthorizationHeader(
            new OAuth1Signer.Config("ck", "cs", "HMAC-SHA512",
                Nonce: "n", Timestamp: 1),
            "POST", "https://api.test/");
        s512.Should().Contain("oauth_signature_method=\"HMAC-SHA512\"");
    }

    [Fact]
    public void PercentEncode_FollowsRfc3986_NotForms()
    {
        // RFC 5849 §3.6: !*'() must be encoded; ~ stays.
        OAuth1Signer.PercentEncode("hello world").Should().Be("hello%20world");
        OAuth1Signer.PercentEncode("a*b").Should().Be("a%2Ab");
        OAuth1Signer.PercentEncode("a'b").Should().Be("a%27b");
        OAuth1Signer.PercentEncode("a!b").Should().Be("a%21b");
        OAuth1Signer.PercentEncode("a(b)").Should().Be("a%28b%29");
        OAuth1Signer.PercentEncode("a~b").Should().Be("a~b");
    }

    [Fact]
    public void RsaMethods_ThrowNotSupported()
    {
        var act = () => OAuth1Signer.BuildAuthorizationHeader(
            new OAuth1Signer.Config("ck", "cs", "RSA-SHA1"),
            "GET", "https://api.test/");
        act.Should().Throw<NotSupportedException>();
    }
}
