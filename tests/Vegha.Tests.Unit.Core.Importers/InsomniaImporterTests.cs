using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class InsomniaImporterTests
{
    [Fact]
    public void V4_FlatResources_RebuildsFolderTreeViaParentId()
    {
        const string json = """
            {
              "_type": "export",
              "__export_format": 4,
              "resources": [
                { "_id": "wsp_1", "_type": "workspace", "name": "Acme API" },
                { "_id": "fld_1", "_type": "request_group", "parentId": "wsp_1", "name": "Users" },
                { "_id": "req_1", "_type": "request", "parentId": "fld_1",
                  "name": "List users", "method": "GET", "url": "{{ _.baseUrl }}/users",
                  "headers": [{ "name": "Accept", "value": "application/json" }] },
                { "_id": "req_2", "_type": "request", "parentId": "wsp_1",
                  "name": "Health", "method": "GET", "url": "{{ _.baseUrl }}/health" }
              ]
            }
            """;

        var c = InsomniaImporter.ImportFromString(json);

        c.Name.Should().Be("Acme API");
        c.Requests.Should().ContainSingle();
        c.Requests[0].Name.Should().Be("Health");
        c.Requests[0].Url.Should().Be("{{baseUrl}}/health"); // _.var stripped
        c.Folders.Should().ContainSingle();
        c.Folders[0].Name.Should().Be("Users");
        c.Folders[0].Requests.Should().ContainSingle();
        c.Folders[0].Requests[0].Name.Should().Be("List users");
    }

    [Fact]
    public void V5_NestedCollection_PopulatesFoldersFromChildren()
    {
        const string json = """
            {
              "type": "collection.insomnia.rest/5.0",
              "name": "Acme",
              "collection": [
                { "name": "Users",
                  "children": [
                    { "name": "List", "method": "GET", "url": "{{ _.baseUrl }}/users",
                      "headers": [{ "name": "Accept", "value": "application/json" }] }
                  ]
                },
                { "name": "Ping", "method": "GET", "url": "{{ _.baseUrl }}/ping" }
              ]
            }
            """;

        var c = InsomniaImporter.ImportFromString(json);

        c.Name.Should().Be("Acme");
        c.Folders.Should().ContainSingle();
        c.Folders[0].Name.Should().Be("Users");
        c.Folders[0].Requests[0].Url.Should().Be("{{baseUrl}}/users");
        c.Requests.Should().ContainSingle();
        c.Requests[0].Name.Should().Be("Ping");
    }

    [Theory]
    [InlineData("application/json", BodyMode.Json)]
    [InlineData("application/x-www-form-urlencoded", BodyMode.FormUrlEncoded)]
    [InlineData("multipart/form-data", BodyMode.MultipartForm)]
    [InlineData("text/plain", BodyMode.Text)]
    [InlineData("application/xml", BodyMode.Xml)]
    public void Body_MimeTypes_MapToBodyModes(string mime, BodyMode expected)
    {
        var json = $$"""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wsp_1", "_type": "workspace", "name": "T" },
                { "_id": "req_1", "_type": "request", "parentId": "wsp_1",
                  "name": "X", "method": "POST", "url": "https://x.test",
                  "body": { "mimeType": "{{mime}}", "text": "{}",
                            "params": [{ "name": "a", "value": "1" }] } }
              ]
            }
            """;

        var c = InsomniaImporter.ImportFromString(json);
        c.Requests[0].Body.Mode.Should().Be(expected);
    }

    [Fact]
    public void Auth_Basic_BearerAndApiKey_RoundTripIntoAuthConfig()
    {
        const string json = """
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wsp_1", "_type": "workspace", "name": "T" },
                { "_id": "r1", "_type": "request", "parentId": "wsp_1",
                  "name": "Basic", "method": "GET", "url": "https://x.test",
                  "authentication": { "type": "basic", "username": "alice", "password": "{{ _.pw }}" } },
                { "_id": "r2", "_type": "request", "parentId": "wsp_1",
                  "name": "Bearer", "method": "GET", "url": "https://x.test",
                  "authentication": { "type": "bearer", "token": "T" } },
                { "_id": "r3", "_type": "request", "parentId": "wsp_1",
                  "name": "ApiKey", "method": "GET", "url": "https://x.test",
                  "authentication": { "type": "apikey", "key": "X-Token", "value": "abc", "addTo": "queryParams" } }
              ]
            }
            """;

        var c = InsomniaImporter.ImportFromString(json);
        var byName = c.Requests.ToDictionary(r => r.Name);

        byName["Basic"].Auth!.Type.Should().Be(AuthType.Basic);
        byName["Basic"].Auth!.Parameters["username"].Should().Be("alice");
        byName["Basic"].Auth!.Parameters["password"].Should().Be("{{pw}}");

        byName["Bearer"].Auth!.Type.Should().Be(AuthType.Bearer);
        byName["Bearer"].Auth!.Parameters["token"].Should().Be("T");

        byName["ApiKey"].Auth!.Type.Should().Be(AuthType.ApiKey);
        byName["ApiKey"].Auth!.Parameters["placement"].Should().Be("queryparams");
        byName["ApiKey"].Auth!.Parameters["value"].Should().Be("abc");
    }

    [Fact]
    public void V4_MissingWorkspace_Throws()
    {
        const string json = """{ "_type": "export", "resources": [{ "_id": "x", "_type": "request" }] }""";
        var act = () => InsomniaImporter.ImportFromString(json);
        act.Should().Throw<InvalidDataException>();
    }
}
