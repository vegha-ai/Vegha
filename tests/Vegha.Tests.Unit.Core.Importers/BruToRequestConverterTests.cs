using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class BruToRequestConverterTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static RequestItem Convert(string fixture) =>
        BruToRequestConverter.Convert(BruParser.Parse(ReadFixture(fixture)));

    // ============================== ping.bru — minimal ==============================

    [Fact]
    public void Ping_Converts_BasicMetadata()
    {
        var req = Convert("ping.bru");
        req.Name.Should().Be("ping");
        req.Method.Should().Be("GET");
        req.Sequence.Should().Be(1);
        req.Url.Should().Be("{{host}}/ping");
        req.Body.Mode.Should().Be(BodyMode.None);
        req.Auth.Should().BeNull();
        req.PreRequestScript.Should().Contain("bru.runner.stopExecution()");
    }

    // ============================== request.bru — comprehensive ==============================

    [Fact]
    public void RequestFixture_Method_Is_GET_FromVerbBlock()
    {
        var req = Convert("request.bru");
        req.Method.Should().Be("GET");
        req.Url.Should().Be("https://api.textlocal.in/send/:id");
    }

    [Fact]
    public void RequestFixture_Headers_PreserveQuotedKeys_AndDisableFlag()
    {
        var req = Convert("request.bru");

        req.Headers.Should().Contain(h => h.Name == "content-type" && h.Value == "application/json" && h.Enabled);
        req.Headers.Should().Contain(h => h.Name == "Authorization" && h.Value == "Bearer 123");
        req.Headers.Should().Contain(h => h.Name == "key with spaces" && h.Enabled);
        req.Headers.Should().Contain(h => h.Name == "{braces}");

        req.Headers.Should().Contain(h => h.Name == "transaction-id" && !h.Enabled);
        req.Headers.Should().Contain(h => h.Name == "disabled:colon:header" && !h.Enabled);
    }

    [Fact]
    public void RequestFixture_QueryParams_AndPathParams()
    {
        var req = Convert("request.bru");

        req.Params.Should().Contain(p => p.Name == "apiKey" && p.Value == "secret");
        req.Params.Should().Contain(p => p.Name == "numbers" && p.Value == "998877665");
        req.Params.Should().Contain(p => p.Name == "message" && !p.Enabled);

        req.PathParams.Should().Contain(p => p.Name == "id" && p.Value == "123");
    }

    [Fact]
    public void RequestFixture_BodyType_FollowsVerbBlock()
    {
        // In request.bru, "get" block declares body: json. Converter selects body:json content.
        var req = Convert("request.bru");
        req.Body.Mode.Should().Be(BodyMode.Json);
        req.Body.Content.Should().Contain("\"hello\": \"world\"");
    }

    [Fact]
    public void RequestFixture_Auth_FollowsVerbBlock()
    {
        var req = Convert("request.bru");
        // verb declares auth: bearer → converter pulls from auth:bearer block
        req.Auth.Should().NotBeNull();
        req.Auth!.Type.Should().Be(AuthType.Bearer);
        req.Auth.Parameters.Should().ContainKey("token");
        req.Auth.Parameters["token"].Should().Be("123");
    }

    [Fact]
    public void RequestFixture_VarsPrePost_AreCarried()
    {
        var req = Convert("request.bru");
        req.PreRequestVars.Should().Contain(v => v.Name == "departingDate" && v.Value == "2020-01-01");
        req.PreRequestVars.Should().Contain(v => v.Name == "returningDate" && !v.Enabled);

        req.PostResponseVars.Should().Contain(v => v.Name == "token" && v.Value == "$res.body.token");
        req.PostResponseVars.Should().Contain(v => v.Name == "@orderNumber"); // @-key, not annotation
    }

    [Fact]
    public void RequestFixture_Scripts_AndDocs_AreCarried()
    {
        var req = Convert("request.bru");
        req.PreRequestScript.Should().Contain("const foo = 'bar'");
        req.Tests.Should().Contain("expect(response.status)");
        req.Docs.Should().Contain("This request needs auth token");
    }

    // ============================== Body type per declaration ==============================

    [Theory]
    [InlineData("text", BodyMode.Text)]
    [InlineData("xml", BodyMode.Xml)]
    [InlineData("json", BodyMode.Json)]
    [InlineData("sparql", BodyMode.Sparql)]
    public void DeclaredBodyType_PicksCorrectBlock(string declared, BodyMode expected)
    {
        var bru = $$"""
            meta {
              name: x
            }

            get {
              url: https://example.com
              body: {{declared}}
              auth: none
            }

            body:{{declared}} {
              hello-content
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Body.Mode.Should().Be(expected);
        req.Body.Content.Should().Be("  hello-content");
    }

    [Fact]
    public void GraphQLBody_PopulatesQueryAndVariables()
    {
        const string bru = """
            meta {
              name: q
            }
            post {
              url: https://example.com/graphql
              body: graphql
              auth: none
            }
            body:graphql {
              { hello }
            }
            body:graphql:vars {
              { "limit": 5 }
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Body.Mode.Should().Be(BodyMode.GraphQL);
        req.Body.GraphQLQuery.Should().Contain("hello");
        req.Body.GraphQLVariables.Should().Contain("\"limit\": 5");
    }

    [Fact]
    public void FormUrlEncoded_BuildsFormData()
    {
        const string bru = """
            meta {
              name: form
            }
            post {
              url: https://example.com
              body: form-urlencoded
              auth: none
            }
            body:form-urlencoded {
              key: value
              ~disabled: x
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Body.Mode.Should().Be(BodyMode.FormUrlEncoded);
        req.Body.FormData.Should().Contain(p => p.Name == "key" && p.Value == "value" && p.Enabled);
        req.Body.FormData.Should().Contain(p => p.Name == "disabled" && !p.Enabled);
    }

    // ============================== Auth flavors ==============================

    [Fact]
    public void Auth_None_LeavesAuthNull()
    {
        const string bru = """
            meta {
              name: x
            }
            get {
              url: https://example.com
              body: none
              auth: none
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Auth.Should().BeNull();
    }

    [Fact]
    public void Auth_Inherit_SetsInheritType()
    {
        const string bru = """
            meta {
              name: x
            }
            get {
              url: https://example.com
              body: none
              auth: inherit
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Auth.Should().NotBeNull();
        req.Auth!.Type.Should().Be(AuthType.Inherit);
    }

    [Fact]
    public void Auth_AwsV4_PullsAllParameters()
    {
        const string bru = """
            meta {
              name: x
            }
            get {
              url: https://example.com
              body: none
              auth: awsv4
            }
            auth:awsv4 {
              accessKeyId: A12345678
              secretAccessKey: secret
              region: us-east-1
              service: execute-api
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Auth!.Type.Should().Be(AuthType.AwsV4);
        req.Auth.Parameters["accessKeyId"].Should().Be("A12345678");
        req.Auth.Parameters["region"].Should().Be("us-east-1");
        req.Auth.Parameters["service"].Should().Be("execute-api");
    }

    // ============================== Custom HTTP method ==============================

    [Fact]
    public void HttpCustom_ReadsMethodFromMethodPair()
    {
        const string bru = """
            meta {
              name: x
            }
            http {
              url: https://example.com
              method: link
              body: none
              auth: none
            }
            """;
        var req = BruToRequestConverter.Convert(BruParser.Parse(bru));
        req.Method.Should().Be("LINK");
    }
}
