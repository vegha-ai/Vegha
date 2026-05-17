using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class PostmanV2ImporterTests
{
    [Fact]
    public void ImportsCollectionName_AndSimpleGet()
    {
        const string json = """
            {
              "info": { "name": "Acme API", "schema": "v2.1.0" },
              "item": [
                {
                  "name": "Get user",
                  "request": {
                    "method": "GET",
                    "url": { "raw": "https://api.acme.io/users/{{userId}}" }
                  }
                }
              ]
            }
            """;

        var col = PostmanV2Importer.ImportFromJson(json);

        col.Name.Should().Be("Acme API");
        col.Requests.Should().HaveCount(1);
        col.Requests[0].Method.Should().Be("GET");
        col.Requests[0].Url.Should().Be("https://api.acme.io/users/{{userId}}");
        col.Requests[0].Sequence.Should().Be(1);
    }

    [Fact]
    public void RecursiveFolders_ProduceNestedFolderTree()
    {
        const string json = """
            {
              "info": { "name": "root" },
              "item": [
                { "name": "top.bru", "request": { "method": "GET", "url": "https://x" } },
                {
                  "name": "Auth",
                  "item": [
                    { "name": "login", "request": { "method": "POST", "url": "https://x/login" } },
                    {
                      "name": "Inner",
                      "item": [
                        { "name": "deep", "request": { "method": "GET", "url": "https://x/deep" } }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var col = PostmanV2Importer.ImportFromJson(json);

        col.Requests.Should().ContainSingle(r => r.Name == "top.bru");
        col.Folders.Should().ContainSingle();
        var auth = col.Folders[0];
        auth.Name.Should().Be("Auth");
        auth.Requests.Should().ContainSingle(r => r.Name == "login");
        auth.Folders.Should().ContainSingle(f => f.Name == "Inner");
        auth.Folders[0].Requests.Should().ContainSingle(r => r.Name == "deep");
    }

    [Fact]
    public void Headers_DisableFlag_PreservesEnabledState()
    {
        const string json = """
            {
              "info": { "name": "x" },
              "item": [{
                "name": "r",
                "request": {
                  "method": "GET",
                  "url": "https://x",
                  "header": [
                    { "key": "Accept", "value": "application/json" },
                    { "key": "X-Off",  "value": "no", "disabled": true }
                  ]
                }
              }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Headers.Should().Contain(h => h.Name == "Accept" && h.Enabled);
        col.Requests[0].Headers.Should().Contain(h => h.Name == "X-Off" && !h.Enabled);
    }

    [Fact]
    public void QueryParams_AndPathVariables_BothExtracted()
    {
        const string json = """
            {
              "info": { "name": "x" },
              "item": [{
                "name": "r",
                "request": {
                  "method": "GET",
                  "url": {
                    "raw": "https://x/users/:id?q=hello&disabled=skip",
                    "query": [
                      { "key": "q", "value": "hello" },
                      { "key": "disabled", "value": "skip", "disabled": true }
                    ],
                    "variable": [
                      { "key": "id", "value": "42" }
                    ]
                  }
                }
              }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        var r = col.Requests[0];
        r.Params.Should().Contain(p => p.Name == "q" && p.Value == "hello" && p.Enabled);
        r.Params.Should().Contain(p => p.Name == "disabled" && !p.Enabled);
        r.PathParams.Should().Contain(p => p.Name == "id" && p.Value == "42");
    }

    [Fact]
    public void RawBody_JsonLanguage_BecomesJsonMode()
    {
        const string json = """
            {
              "info": { "name": "x" },
              "item": [{
                "name": "r",
                "request": {
                  "method": "POST", "url": "https://x",
                  "body": {
                    "mode": "raw",
                    "raw": "{\"hi\":1}",
                    "options": { "raw": { "language": "json" } }
                  }
                }
              }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Body.Mode.Should().Be(BodyMode.Json);
        col.Requests[0].Body.Content.Should().Be("{\"hi\":1}");
    }

    [Fact]
    public void RawBody_XmlLanguage_BecomesXmlMode()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "POST", "url": "https://x",
                "body": { "mode": "raw", "raw": "<root/>", "options": { "raw": { "language": "xml" } } } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Body.Mode.Should().Be(BodyMode.Xml);
    }

    [Fact]
    public void UrlEncoded_FormPairs_PreserveDisabled()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "POST", "url": "https://x",
                "body": { "mode": "urlencoded", "urlencoded": [
                  { "key": "a", "value": "1" },
                  { "key": "b", "value": "2", "disabled": true }
                ] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Body.Mode.Should().Be(BodyMode.FormUrlEncoded);
        col.Requests[0].Body.FormData.Should().Contain(p => p.Name == "a" && p.Enabled);
        col.Requests[0].Body.FormData.Should().Contain(p => p.Name == "b" && !p.Enabled);
    }

    [Fact]
    public void GraphQLBody_QueryAndVariablesExtracted()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "POST", "url": "https://x/graphql",
                "body": { "mode": "graphql", "graphql": {
                  "query": "{ hello }",
                  "variables": "{ \"limit\": 5 }"
                } } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Body.Mode.Should().Be(BodyMode.GraphQL);
        col.Requests[0].Body.GraphQLQuery.Should().Be("{ hello }");
        col.Requests[0].Body.GraphQLVariables.Should().Contain("\"limit\"");
    }

    [Fact]
    public void Auth_Basic_MapsToBasicAuthConfig()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "basic", "basic": [
                  { "key": "username", "value": "alice" },
                  { "key": "password", "value": "s3cret" }
                ] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        var auth = col.Requests[0].Auth!;
        auth.Type.Should().Be(AuthType.Basic);
        auth.Parameters["username"].Should().Be("alice");
        auth.Parameters["password"].Should().Be("s3cret");
    }

    [Fact]
    public void Auth_Bearer_MapsToBearerAuthConfig()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "tk" }] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Auth!.Type.Should().Be(AuthType.Bearer);
        col.Requests[0].Auth!.Parameters["token"].Should().Be("tk");
    }

    [Fact]
    public void Auth_ApiKey_QueryPlacement_MapsCorrectly()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "apikey", "apikey": [
                  { "key": "key", "value": "X-Api" },
                  { "key": "value", "value": "v" },
                  { "key": "in", "value": "query" }
                ] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        var auth = col.Requests[0].Auth!;
        auth.Type.Should().Be(AuthType.ApiKey);
        auth.Parameters["placement"].Should().Be("queryparams");
    }

    [Fact]
    public void Auth_AwsV4_MapsAccessKeyAndRegion()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "awsv4", "awsv4": [
                  { "key": "accessKey", "value": "AKIA" },
                  { "key": "secretKey", "value": "secret" },
                  { "key": "region", "value": "us-east-1" },
                  { "key": "service", "value": "execute-api" }
                ] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        var auth = col.Requests[0].Auth!;
        auth.Type.Should().Be(AuthType.AwsV4);
        auth.Parameters["accessKeyId"].Should().Be("AKIA");
        auth.Parameters["region"].Should().Be("us-east-1");
        auth.Parameters["service"].Should().Be("execute-api");
    }

    [Fact]
    public void Auth_OAuth2_MapsClientCredentials()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "oauth2", "oauth2": [
                  { "key": "grant_type", "value": "client_credentials" },
                  { "key": "accessTokenUrl", "value": "https://idp/token" },
                  { "key": "clientId", "value": "cid" },
                  { "key": "clientSecret", "value": "csec" },
                  { "key": "scope", "value": "read" },
                  { "key": "client_authentication", "value": "header" }
                ] } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        var auth = col.Requests[0].Auth!;
        auth.Type.Should().Be(AuthType.OAuth2);
        auth.Parameters["access_token_url"].Should().Be("https://idp/token");
        auth.Parameters["client_id"].Should().Be("cid");
        auth.Parameters["credentials_placement"].Should().Be("basic_auth_header");
    }

    [Fact]
    public void Events_PrerequestAndTests_BecomeScripts()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r",
              "event": [
                { "listen": "prerequest", "script": { "exec": ["pm.environment.set('a', '1');"] } },
                { "listen": "test",       "script": { "exec": ["pm.test('s', function(){});"] } }
              ],
              "request": { "method": "GET", "url": "https://x" }
            }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].PreRequestScript.Should().Contain("pm.environment.set");
        col.Requests[0].Tests.Should().Contain("pm.test");
    }

    [Fact]
    public void CollectionVariables_AreExtracted()
    {
        const string json = """
            {
              "info": { "name": "x" },
              "variable": [
                { "key": "baseUrl", "value": "https://api.acme.io" },
                { "key": "apiKey",  "value": "secret" }
              ],
              "item": []
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Variables.Should().Contain(v => v.Name == "baseUrl" && v.Value == "https://api.acme.io");
        col.Variables.Should().Contain(v => v.Name == "apiKey" && v.Value == "secret");
    }

    [Fact]
    public void NoAuth_LeavesAuthNull()
    {
        const string json = """
            { "info": { "name": "x" }, "item": [{
              "name": "r", "request": { "method": "GET", "url": "https://x",
                "auth": { "type": "noauth" } } }]
            }
            """;
        var col = PostmanV2Importer.ImportFromJson(json);
        col.Requests[0].Auth.Should().BeNull();
    }
}
