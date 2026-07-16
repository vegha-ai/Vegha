using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class BruEmitterTests
{
    private static RequestItem RoundTrip(RequestItem original)
    {
        var bru = BruEmitter.Emit(original);
        var doc = BruParser.Parse(bru);
        return BruToRequestConverter.Convert(doc);
    }

    [Fact]
    public void RoundTrip_Minimal_Get()
    {
        var original = new RequestItem
        {
            Name = "ping",
            Method = "GET",
            Url = "https://example.com/ping",
            Sequence = 1,
        };

        var rt = RoundTrip(original);

        rt.Name.Should().Be("ping");
        rt.Method.Should().Be("GET");
        rt.Url.Should().Be("https://example.com/ping");
        rt.Sequence.Should().Be(1);
    }

    [Fact]
    public void RoundTrip_HeadersAndParams_PreserveDisableFlag()
    {
        var original = new RequestItem
        {
            Name = "x",
            Method = "GET",
            Url = "https://x.test/y",
            Headers = new List<KvPair>
            {
                new("Accept", "application/json"),
                new("X-Disabled", "no", enabled: false)
            },
            Params = new List<KvPair>
            {
                new("q", "hello"),
                new("limit", "5"),
            }
        };

        var rt = RoundTrip(original);

        rt.Headers.Should().Contain(h => h.Name == "Accept" && h.Value == "application/json" && h.Enabled);
        rt.Headers.Should().Contain(h => h.Name == "X-Disabled" && !h.Enabled);
        rt.Params.Should().Contain(p => p.Name == "q" && p.Value == "hello");
        rt.Params.Should().Contain(p => p.Name == "limit" && p.Value == "5");
    }

    [Fact]
    public void RoundTrip_QuotedKeys_Preserved()
    {
        var original = new RequestItem
        {
            Name = "x",
            Method = "GET",
            Url = "https://x.test/y",
            Headers = new List<KvPair>
            {
                new("key with spaces", "is allowed"),
                new("colon:header", "is allowed"),
                new("{braces}", "is allowed")
            }
        };

        var rt = RoundTrip(original);
        rt.Headers.Should().Contain(h => h.Name == "key with spaces");
        rt.Headers.Should().Contain(h => h.Name == "colon:header");
        rt.Headers.Should().Contain(h => h.Name == "{braces}");
    }

    [Fact]
    public void RoundTrip_BearerAuth()
    {
        var original = new RequestItem
        {
            Name = "x",
            Method = "GET",
            Url = "https://x.test/y",
            Auth = new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new Dictionary<string, string> { ["token"] = "abc123" }
            }
        };

        var rt = RoundTrip(original);
        rt.Auth.Should().NotBeNull();
        rt.Auth!.Type.Should().Be(AuthType.Bearer);
        rt.Auth.Parameters["token"].Should().Be("abc123");
    }

    [Fact]
    public void RoundTrip_BasicAuth()
    {
        var original = new RequestItem
        {
            Name = "x",
            Method = "POST",
            Url = "https://x.test/y",
            Auth = new AuthConfig
            {
                Type = AuthType.Basic,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = "alice",
                    ["password"] = "s3cret",
                }
            }
        };

        var rt = RoundTrip(original);
        rt.Auth!.Type.Should().Be(AuthType.Basic);
        rt.Auth.Parameters["username"].Should().Be("alice");
        rt.Auth.Parameters["password"].Should().Be("s3cret");
    }

    [Fact]
    public void RoundTrip_AwsV4Auth_AllParameters()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "POST", Url = "https://x.test/y",
            Auth = new AuthConfig
            {
                Type = AuthType.AwsV4,
                Parameters = new Dictionary<string, string>
                {
                    ["accessKeyId"] = "AKIA1",
                    ["secretAccessKey"] = "secret",
                    ["region"] = "us-east-1",
                    ["service"] = "execute-api"
                }
            }
        };

        var rt = RoundTrip(original);
        rt.Auth!.Type.Should().Be(AuthType.AwsV4);
        rt.Auth.Parameters.Should().HaveCount(4);
    }

    [Fact]
    public void RoundTrip_JsonBody_PreservesContent()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "POST", Url = "https://x.test/y",
            Body = new BodyConfig { Mode = BodyMode.Json, Content = "  {\n    \"hello\": \"world\"\n  }" }
        };

        var rt = RoundTrip(original);
        rt.Body.Mode.Should().Be(BodyMode.Json);
        rt.Body.Content.Should().Contain("\"hello\": \"world\"");
    }

    [Fact]
    public void RoundTrip_FormUrlEncoded_Preserved()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "POST", Url = "https://x.test/y",
            Body = new BodyConfig
            {
                Mode = BodyMode.FormUrlEncoded,
                FormData = new List<KvPair>
                {
                    new("apikey", "secret"),
                    new("disabled", "x", enabled: false)
                }
            }
        };

        var rt = RoundTrip(original);
        rt.Body.Mode.Should().Be(BodyMode.FormUrlEncoded);
        rt.Body.FormData.Should().Contain(p => p.Name == "apikey" && p.Enabled);
        rt.Body.FormData.Should().Contain(p => p.Name == "disabled" && !p.Enabled);
    }

    [Fact]
    public void RoundTrip_VarsScriptsTestsDocs_AllPreserved()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "GET", Url = "https://x.test/y",
            PreRequestVars = new List<KvPair> { new("departing", "2026-01-01") },
            PostResponseVars = new List<KvPair> { new("token", "$res.body.token") },
            PreRequestScript = "  const foo = 'bar';",
            Tests = "  expect(response.status).to.equal(200);",
            Docs = "  Hello docs."
        };

        var rt = RoundTrip(original);
        rt.PreRequestVars.Should().Contain(v => v.Name == "departing");
        rt.PostResponseVars.Should().Contain(v => v.Name == "token");
        rt.PreRequestScript.Should().Contain("const foo");
        rt.Tests.Should().Contain("expect(response.status)");
        rt.Docs.Should().Contain("Hello docs");
    }

    [Fact]
    public void Emit_NoBody_NoAuth_OmitsThoseBlocks()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "GET", Url = "https://x.test/y",
        };
        var bru = BruEmitter.Emit(original);
        // Verb declares "body: none" / "auth: none" but no body:* / auth:* sibling blocks emitted.
        bru.Should().Contain("auth: none");
        bru.Should().Contain("body: none");
        bru.Should().NotContain("body:json");
        bru.Should().NotContain("body:text");
        bru.Should().NotContain("body:xml");
        bru.Should().NotContain("auth:bearer");
        bru.Should().NotContain("auth:basic");
        bru.Should().NotContain("auth:apikey");
    }

    [Fact]
    public void RoundTrip_Settings_NonDefaultValuesPersist()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "GET", Url = "https://x.test/y",
            Settings = new RequestSettingsConfig
            {
                FollowRedirects = false,
                VerifySsl = false,
                EncodeUrl = true,    // default — should NOT be emitted
                SendCookies = false,
            }
        };

        var bru = BruEmitter.Emit(original);
        bru.Should().Contain("settings {");
        bru.Should().Contain("followRedirects: false");
        bru.Should().Contain("verifySSL: false");
        bru.Should().Contain("sendCookies: false");
        bru.Should().NotContain("encodeUrl: true"); // default suppressed

        var rt = RoundTrip(original);
        rt.Settings.FollowRedirects.Should().BeFalse();
        rt.Settings.VerifySsl.Should().BeFalse();
        rt.Settings.SendCookies.Should().BeFalse();
        rt.Settings.EncodeUrl.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_Settings_MtlsClientCertPersists()
    {
        var original = new RequestItem
        {
            Name = "x", Method = "GET", Url = "https://x.test/y",
            Settings = new RequestSettingsConfig
            {
                MtlsCertPath = "/certs/client.p12",
                MtlsCertPassword = "{{certPassword}}",
            }
        };

        var bru = BruEmitter.Emit(original);
        bru.Should().Contain("mtlsCertPath: /certs/client.p12");
        bru.Should().Contain("mtlsCertPassword: {{certPassword}}");

        var rt = RoundTrip(original);
        rt.Settings.MtlsCertPath.Should().Be("/certs/client.p12");
        rt.Settings.MtlsCertPassword.Should().Be("{{certPassword}}");
        rt.Settings.VerifySsl.Should().BeTrue("other settings keep their defaults");
    }

    [Fact]
    public void Emit_AllDefaultSettings_OmitsSettingsBlock()
    {
        var bru = BruEmitter.Emit(new RequestItem
        {
            Name = "x", Method = "GET", Url = "https://x", // default Settings
        });
        bru.Should().NotContain("settings {");
    }

    [Fact]
    public void Emit_FromImportedFixture_ProducesParseableOutput()
    {
        // Read Bruno's request.bru, convert to RequestItem, emit, reparse — verify no errors.
        var bru = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "request.bru"));
        var imported = BruToRequestConverter.Convert(BruParser.Parse(bru));

        var emitted = BruEmitter.Emit(imported);
        BruParser.TryParse(emitted, out var doc, out var error)
            .Should().BeTrue($"emitted should parse: {error}");
        doc.Blocks.Should().NotBeEmpty();
    }
}
