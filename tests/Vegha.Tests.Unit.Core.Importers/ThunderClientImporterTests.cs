using System.Text;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public sealed class ThunderClientImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vegha-tc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string CollectionExport = """
        {
          "client": "Thunder Client",
          "collectionName": "Shop API",
          "dateExported": "2026-05-01T10:00:00.000Z",
          "version": "1.2",
          "folders": [
            { "_id": "f1", "name": "Users", "containerId": "", "sortNum": 10000,
              "settings": { "headers": [{ "name": "X-Folder", "value": "users" }],
                            "auth": { "type": "bearer", "bearer": "{{token}}" } } },
            { "_id": "f2", "name": "Admin", "containerId": "f1", "sortNum": 20000 }
          ],
          "requests": [
            { "_id": "r1", "colId": "c1", "containerId": "f1", "name": "Get user", "url": "{{baseUrl}}/users/{id}?verbose=true",
              "method": "GET", "sortNum": 20000,
              "headers": [{ "name": "Accept", "value": "application/json" }, { "name": "X-Debug", "value": "1", "isDisabled": true }],
              "params": [{ "name": "verbose", "value": "true", "isPath": false }, { "name": "id", "value": "42", "isPath": true }],
              "tests": [
                { "type": "res-code", "custom": "", "action": "equal", "value": "200" },
                { "type": "json-query", "custom": "json.name", "action": "equal", "value": "Ada" },
                { "type": "json-query", "custom": "json.roles", "action": "count", "value": "2" },
                { "type": "Content-Type", "custom": "", "action": "contains", "value": "application/json" },
                { "type": "res-time", "custom": "", "action": "<", "value": "500" },
                { "type": "json-query", "custom": "json.id", "action": "istype", "value": "number" },
                { "type": "set-env-var", "custom": "json.token", "action": "setto", "value": "{{authToken}}" },
                { "type": "set-env-var", "custom": "header.x-trace", "action": "setto", "value": "{{trace, global}}" }
              ] },
            { "_id": "r0", "colId": "c1", "containerId": "f1", "name": "Create user", "url": "{{baseUrl}}/users",
              "method": "POST", "sortNum": 10000,
              "body": { "type": "json", "raw": "{\"name\":\"{{#name}}\"}", "form": [] },
              "auth": { "type": "basic", "basic": { "username": "admin", "password": "{{adminPassword}}" } },
              "postReq": { "inlineScripts": [{ "script": ["const u = tc.response.json;", "tc.setVar('userId', u.id);"] }] } },
            { "_id": "r2", "colId": "c1", "containerId": "", "name": "Upload", "url": "{{baseUrl}}/files", "method": "post",
              "body": { "type": "formdata", "form": [{ "name": "title", "value": "x" }], "files": [{ "name": "doc", "value": "./a.pdf" }] } },
            { "_id": "r3", "colId": "c1", "containerId": "f2", "name": "Graph", "url": "{{baseUrl}}/graphql", "method": "POST",
              "body": { "type": "graphql", "graphql": { "query": "{ me { id } }", "variables": "{}" } } }
          ],
          "settings": { "headers": [{ "name": "User-Agent", "value": "vegha" }],
                        "auth": { "type": "apikey", "apikey": { "key": "X-Key", "value": "{{apiKey}}", "addTo": "header" } },
                        "options": { "baseUrl": "https://api.example.com" } }
        }
        """;

    [Fact]
    public void Collection_export_rebuilds_folders_requests_and_order()
    {
        var c = ThunderClientImporter.ImportCollection(CollectionExport);

        c.Name.Should().Be("Shop API");
        c.Requests.Select(r => r.Name).Should().Equal("Upload");
        c.Folders.Should().ContainSingle().Which.Name.Should().Be("Users");
        var users = c.Folders[0];
        users.Requests.Select(r => r.Name).Should().Equal("Create user", "Get user"); // sortNum order
        users.Folders.Should().ContainSingle().Which.Requests.Single().Name.Should().Be("Graph");
        users.Headers.Should().ContainSingle(h => h.Name == "X-Folder");
        users.Auth!.Type.Should().Be(AuthType.Bearer);
        users.Auth.Parameters["token"].Should().Be("{{token}}");
        c.Headers.Should().ContainSingle(h => h.Name == "User-Agent");
        c.Auth!.Type.Should().Be(AuthType.ApiKey);
        c.Auth.Parameters["key"].Should().Be("X-Key");
        c.Presets!.BaseUrl.Should().Be("https://api.example.com");
    }

    [Fact]
    public void Request_params_headers_and_path_params_map_to_Vegha()
    {
        var get = ThunderClientImporter.ImportCollection(CollectionExport).Folders[0].Requests.Single(r => r.Name == "Get user");

        get.Url.Should().Be("{{baseUrl}}/users/:id?verbose=true");
        get.PathParams.Should().ContainSingle(p => p.Name == "id" && p.Value == "42");
        get.Params.Should().ContainSingle(p => p.Name == "verbose");
        get.Headers.Single(h => h.Name == "X-Debug").Enabled.Should().BeFalse();
        get.Auth!.Type.Should().Be(AuthType.Inherit); // Thunder's default
    }

    [Fact]
    public void Scriptless_tests_become_assertions_and_captures()
    {
        var get = ThunderClientImporter.ImportCollection(CollectionExport).Folders[0].Requests.Single(r => r.Name == "Get user");

        get.Assertions.Select(a => $"{a.Name}: {a.Value}").Should().Equal(
            "res.status: eq 200",
            "res.body.name: eq \"Ada\"",
            "res.body.roles: length 2",
            "res.headers['content-type']: contains \"application/json\"",
            "res.responseTime: lt 500",
            "res.body.id: isNumber");
        get.PostResponseEnvVars.Should().ContainSingle(v => v.Name == "authToken" && v.Value == "res.body.token");
        get.PostResponseGlobalVars.Should().ContainSingle(v => v.Name == "trace" && v.Value == "res.headers['x-trace']");
    }

    [Fact]
    public void Bodies_auth_and_scripts_carry_over()
    {
        var c = ThunderClientImporter.ImportCollection(CollectionExport);
        var create = c.Folders[0].Requests.Single(r => r.Name == "Create user");
        create.Body.Mode.Should().Be(BodyMode.Json);
        create.Body.Content.Should().Contain("{{#name}}"); // Thunder system variables resolve natively
        create.Auth!.Type.Should().Be(AuthType.Basic);
        create.Auth.Parameters["password"].Should().Be("{{adminPassword}}");
        // tc.* scripts are kept, commented out, so they can't fail at send time.
        create.PostResponseScript.Should().Contain("// tc.setVar('userId', u.id);");
        create.PostResponseScript!.Split('\n').Should().OnlyContain(l => l.TrimStart().StartsWith("//"));

        var upload = c.Requests.Single();
        upload.Method.Should().Be("POST");
        upload.Body.Mode.Should().Be(BodyMode.MultipartForm);
        upload.Body.FormData.Select(f => $"{f.Name}={f.Value}").Should().Equal("title=x", "doc=@file(./a.pdf)");

        var graph = c.Folders[0].Folders[0].Requests.Single();
        graph.Kind.Should().Be(RequestKind.GraphQL);
        graph.Body.GraphQLQuery.Should().Be("{ me { id } }");
    }

    [Fact]
    public void Environment_export_keeps_secret_flags()
    {
        const string json = """
            { "client": "Thunder Client", "environmentName": "dev", "version": "1.2",
              "variables": [ { "name": "baseUrl", "value": "http://localhost" }, { "name": "token", "value": "s3cr3t", "isSecret": true } ] }
            """;
        var env = ThunderClientImporter.ImportEnvironment(json);
        env.Name.Should().Be("dev");
        env.Variables.Should().HaveCount(2);
        env.SecretVariables.Should().Equal("token");
    }

    [Fact]
    public void Pipeline_detects_both_exports()
    {
        var col = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(CollectionExport), "thunder-collection_Shop API.json");
        col.FormatLabel.Should().Be(ThunderClientImporter.FormatLabel);
        col.Collection!.Name.Should().Be("Shop API");

        var env = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(
            """{ "client": "Thunder Client", "environmentName": "dev", "variables": [] }"""), "thunder-environment_dev.json");
        env.Environment!.Name.Should().Be("dev");
        env.Collection.Should().BeNull();
    }

    [Fact]
    public void Legacy_git_sync_folder_imports_every_collection_and_environment()
    {
        var tt = Path.Combine(_dir, "my-repo", "thunder-tests");
        Directory.CreateDirectory(tt);
        File.WriteAllText(Path.Combine(tt, "thunderCollection.json"), """
            [ { "_id": "c1", "colName": "Users", "sortNum": 1, "folders": [] },
              { "_id": "c2", "colName": "Orders", "sortNum": 2, "folders": [{ "_id": "f1", "name": "Admin", "containerId": "" }] } ]
            """);
        File.WriteAllText(Path.Combine(tt, "thunderclient.json"), """
            [ { "_id": "r1", "colId": "c1", "containerId": "", "name": "List", "url": "{{baseUrl}}/users", "method": "GET" },
              { "_id": "r2", "colId": "c2", "containerId": "f1", "name": "Refund", "url": "{{baseUrl}}/refund", "method": "POST" } ]
            """);
        File.WriteAllText(Path.Combine(tt, "thunderEnvironment.json"), """
            [ { "_id": "e1", "name": "dev", "data": [{ "name": "baseUrl", "value": "http://localhost" }] } ]
            """);

        var result = ImportPipeline.DetectAndImportPath(Path.Combine(_dir, "my-repo"));

        result.Success.Should().BeTrue(result.Error);
        var c = result.Collection!;
        c.Name.Should().Be("my-repo");
        c.Folders.Select(f => f.Name).Should().Equal("Users", "Orders");
        c.Folders[0].Requests.Single().Name.Should().Be("List");
        c.Folders[1].Folders.Single().Requests.Single().Name.Should().Be("Refund");
        c.Environments.Should().ContainSingle(e => e.Name == "dev" && e.Variables.Count == 1);
    }

    [Fact]
    public void Per_file_git_sync_folder_with_one_collection_imports_it_directly()
    {
        var tt = Path.Combine(_dir, "thunder-tests");
        Directory.CreateDirectory(Path.Combine(tt, "collections"));
        Directory.CreateDirectory(Path.Combine(tt, "environments"));
        File.WriteAllText(Path.Combine(tt, "collections", "tc_col_shop.json"), """
            { "_id": "c1", "colName": "Shop", "folders": [],
              "requests": [{ "_id": "r1", "colId": "c1", "containerId": "", "name": "Ping", "url": "http://x/ping", "method": "GET" }] }
            """);
        File.WriteAllText(Path.Combine(tt, "environments", "tc_env_dev.json"), """
            { "_id": "e1", "name": "dev", "data": [{ "name": "a", "value": "1" }] }
            """);

        var c = ThunderClientImporter.ImportGitSyncFolder(tt);

        c.Name.Should().Be("Shop");
        c.Requests.Single().Name.Should().Be("Ping");
        c.Environments.Single().Name.Should().Be("dev");
    }

    [Fact]
    public void Imported_collection_round_trips_through_bru()
    {
        var c = ThunderClientImporter.ImportCollection(CollectionExport);
        var get = c.Folders[0].Requests.Single(r => r.Name == "Get user");

        var text = BruEmitter.Emit(get);
        BruParser.TryParse(text, out var doc, out var error).Should().BeTrue(error);
        var back = BruToRequestConverter.Convert(doc!);
        back.Assertions.Select(a => a.Name).Should().Equal(get.Assertions.Select(a => a.Name));
        back.PostResponseEnvVars.Should().BeEquivalentTo(get.PostResponseEnvVars);
        back.Url.Should().Be(get.Url);
    }
}
