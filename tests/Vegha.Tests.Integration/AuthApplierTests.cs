using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class AuthApplierTests
{
    [Fact]
    public void None_ReturnsUrlUnchanged_NoHeaders()
    {
        var r = AuthApplier.Apply(null, "https://x.test/y");
        r.Url.Should().Be("https://x.test/y");
        r.Headers.Should().BeEmpty();

        var r2 = AuthApplier.Apply(new AuthConfig { Type = AuthType.None }, "https://x.test/y");
        r2.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Inherit_NoOp()
    {
        var r = AuthApplier.Apply(new AuthConfig { Type = AuthType.Inherit }, "https://x.test/y");
        r.Url.Should().Be("https://x.test/y");
        r.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Bearer_ProducesAuthorizationHeader()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = "abc123" }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Headers.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("Authorization", "Bearer abc123"));
    }

    [Fact]
    public void Bearer_ResolvesPlaceholders()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = "{{access_token}}" }
        };
        var vars = new Dictionary<string, string> { ["access_token"] = "from-env" };
        var r = AuthApplier.Apply(auth, "https://x.test/y", vars);
        r.Headers.Single().Value.Should().Be("Bearer from-env");
    }

    [Fact]
    public void Basic_ProducesBase64Encoded_AuthorizationHeader()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.Basic,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = "alice",
                ["password"] = "s3cret"
            }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        // base64("alice:s3cret") = "YWxpY2U6czNjcmV0"
        r.Headers.Single().Should().Be(
            new KeyValuePair<string, string>("Authorization", "Basic YWxpY2U6czNjcmV0"));
    }

    [Fact]
    public void ApiKey_DefaultPlacement_IsHeader()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = "X-API-Key",
                ["value"] = "secret-key"
            }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Headers.Single().Should().Be(new KeyValuePair<string, string>("X-API-Key", "secret-key"));
        r.Url.Should().Be("https://x.test/y");
    }

    [Fact]
    public void ApiKey_QueryPlacement_AppendsToUrl()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = "api_key",
                ["value"] = "secret",
                ["placement"] = "queryparams"
            }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y?existing=1");
        r.Url.Should().Be("https://x.test/y?existing=1&api_key=secret");
        r.Headers.Should().BeEmpty();
    }

    [Fact]
    public void ApiKey_QueryPlacement_AddsLeadingQuestionMark_WhenNoExistingQuery()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = "k",
                ["value"] = "v",
                ["placement"] = "queryparams"
            }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Url.Should().Be("https://x.test/y?k=v");
    }

    [Fact]
    public void ApiKey_UrlEncodesNameAndValue()
    {
        var auth = new AuthConfig
        {
            Type = AuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = "x y",
                ["value"] = "a/b+c",
                ["placement"] = "queryparams"
            }
        };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Url.Should().Be("https://x.test/y?x%20y=a%2Fb%2Bc");
    }

    [Fact]
    public void Bearer_EmptyToken_ProducesNoHeader()
    {
        var auth = new AuthConfig { Type = AuthType.Bearer };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Unsupported_Auth_NoOp()
    {
        var auth = new AuthConfig { Type = AuthType.Digest };
        var r = AuthApplier.Apply(auth, "https://x.test/y");
        r.Headers.Should().BeEmpty();
        r.Url.Should().Be("https://x.test/y");
    }
}
