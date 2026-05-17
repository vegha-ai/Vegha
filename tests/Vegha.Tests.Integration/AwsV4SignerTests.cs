using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class AwsV4SignerTests
{
    // The hard-coded AWS "get-vanilla" test vector uses a slimmer SignedHeaders set than what we
    // emit (we always include x-amz-content-sha256, which modern AWS services require). So we
    // assert the structural properties of the signature (algorithm, scope, signed-headers list,
    // credential format, stability for same inputs, divergence for different inputs) rather
    // than a specific signature byte string. Empty-body SHA-256 from the AWS spec is hard-coded
    // because that's a pure SHA-256 fact, not a SigV4-vector fact.
    //   https://docs.aws.amazon.com/general/latest/gr/sigv4-create-canonical-request.html

    private const string TestAccessKey = "AKIDEXAMPLE";
    private const string TestSecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTime FixedDate = new(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);
    private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Authorization_HasExpectedStructure()
    {
        var output = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            Method: "GET",
            Url: new Uri("https://example.amazonaws.com/"),
            Headers: Array.Empty<KeyValuePair<string, string>>(),
            Body: string.Empty,
            AccessKeyId: TestAccessKey,
            SecretAccessKey: TestSecretKey,
            Region: "us-east-1",
            Service: "service",
            RequestUtcOverride: FixedDate));

        output.Authorization.Should().StartWith("AWS4-HMAC-SHA256 ");
        output.Authorization.Should().Contain($"Credential={TestAccessKey}/20150830/us-east-1/service/aws4_request");
        output.Authorization.Should().Contain("SignedHeaders=host;x-amz-content-sha256;x-amz-date");
        output.Authorization.Should().MatchRegex(@"Signature=[a-f0-9]{64}$");
        output.XAmzDate.Should().Be("20150830T123600Z");
        output.XAmzContentSha256.Should().Be(EmptyBodySha256);
    }

    [Fact]
    public void SameInputs_ProduceIdenticalSignature_Stability()
    {
        var inputs = new AwsV4Signer.Inputs(
            "GET", new Uri("https://example.amazonaws.com/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            TestAccessKey, TestSecretKey, "us-east-1", "service",
            RequestUtcOverride: FixedDate);

        var a = AwsV4Signer.Sign(inputs).Authorization;
        var b = AwsV4Signer.Sign(inputs).Authorization;
        a.Should().Be(b);
    }

    [Fact]
    public void Authorization_StartsWith_AlgorithmIdentifier()
    {
        var output = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "GET", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate));

        output.Authorization.Should().StartWith("AWS4-HMAC-SHA256 Credential=");
    }

    [Fact]
    public void SessionToken_AddsSecurityTokenHeader_AndIncludesItInSignedHeaders()
    {
        var output = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "GET", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            "ak", "sk", "us-east-1", "service",
            SessionToken: "session-tok-1",
            RequestUtcOverride: FixedDate));

        output.XAmzSecurityToken.Should().Be("session-tok-1");
        output.Authorization.Should().Contain("x-amz-security-token");
    }

    [Fact]
    public void Body_HashedIntoSignature_DifferentBody_ChangesSignature()
    {
        var s1 = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "POST", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), "{\"a\":1}",
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate)).Authorization;

        var s2 = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "POST", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), "{\"a\":2}",
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate)).Authorization;

        s1.Should().NotBe(s2);
    }

    [Fact]
    public void QueryString_Sorted_ProducesStableSignature()
    {
        var s1 = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "GET", new Uri("https://x.test/?b=2&a=1"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate)).Authorization;

        var s2 = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "GET", new Uri("https://x.test/?a=1&b=2"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate)).Authorization;

        // Same canonical query (sorted) should yield same signature.
        s1.Should().Be(s2);
    }

    [Fact]
    public void DifferentRegion_ChangesSignature()
    {
        var inputs = new AwsV4Signer.Inputs(
            "GET", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            "ak", "sk", "us-east-1", "service",
            RequestUtcOverride: FixedDate);

        var east = AwsV4Signer.Sign(inputs).Authorization;
        var west = AwsV4Signer.Sign(inputs with { Region = "us-west-2" }).Authorization;
        east.Should().NotBe(west);
        west.Should().Contain("us-west-2");
    }

    [Fact]
    public void SignFromAuthConfig_BridgesAuthConfigToInputs()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.AwsV4,
            Parameters = new Dictionary<string, string>
            {
                ["accessKeyId"]     = TestAccessKey,
                ["secretAccessKey"] = TestSecretKey,
                ["region"]          = "us-east-1",
                ["service"]         = "service",
            }
        };

        var fromConfig = AwsV4Signer.SignFromAuthConfig(
            auth,
            "GET",
            new Uri("https://example.amazonaws.com/"),
            Array.Empty<KeyValuePair<string, string>>(),
            string.Empty,
            utcOverride: FixedDate);

        var direct = AwsV4Signer.Sign(new AwsV4Signer.Inputs(
            "GET", new Uri("https://example.amazonaws.com/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty,
            TestAccessKey, TestSecretKey, "us-east-1", "service",
            RequestUtcOverride: FixedDate));

        fromConfig.Should().NotBeNull();
        fromConfig!.Authorization.Should().Be(direct.Authorization);
    }

    [Fact]
    public void SignFromAuthConfig_MissingRequiredField_ReturnsNull()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.AwsV4,
            Parameters = new Dictionary<string, string>
            {
                ["accessKeyId"] = "x",
                // secret missing
                ["region"] = "us-east-1",
                ["service"] = "s3",
            }
        };

        AwsV4Signer.SignFromAuthConfig(
            auth, "GET", new Uri("https://x.test/"),
            Array.Empty<KeyValuePair<string, string>>(), string.Empty)
            .Should().BeNull();
    }

    [Fact]
    public void SignFromAuthConfig_InterpolatesPlaceholdersFromVars()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.AwsV4,
            Parameters = new Dictionary<string, string>
            {
                ["accessKeyId"]     = "{{aws_id}}",
                ["secretAccessKey"] = "{{aws_secret}}",
                ["region"]          = "us-east-1",
                ["service"]         = "service",
            }
        };
        var vars = new Dictionary<string, string>
        {
            ["aws_id"] = TestAccessKey,
            ["aws_secret"] = TestSecretKey,
        };

        var output = AwsV4Signer.SignFromAuthConfig(
            auth, "GET",
            new Uri("https://example.amazonaws.com/"),
            Array.Empty<KeyValuePair<string, string>>(),
            string.Empty,
            vars,
            utcOverride: FixedDate);

        output!.Authorization.Should().Contain($"Credential={TestAccessKey}/");
    }
}
