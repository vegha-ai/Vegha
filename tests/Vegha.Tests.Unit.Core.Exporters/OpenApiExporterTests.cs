using System.Text.Json;
using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Exporters;

public class OpenApiExporterTests
{
    private static string Export(bool yaml = false, List<string>? warnings = null) =>
        ExportFixtures.Text(OpenApiExporter.Export(
            ExportFixtures.PetStore(), new ExportOptions(OpenApiYaml: yaml), warnings ?? new List<string>()));

    [Fact]
    public void Emits_openapi_30_with_server_paths_and_security()
    {
        using var doc = JsonDocument.Parse(Export());
        var root = doc.RootElement;
        root.GetProperty("openapi").GetString().Should().StartWith("3.0");
        root.GetProperty("info").GetProperty("title").GetString().Should().Be("Pet Store");
        root.GetProperty("info").GetProperty("version").GetString().Should().Be("2.0");
        root.GetProperty("servers")[0].GetProperty("url").GetString().Should().Be("https://api.example.com/v1");

        var paths = root.GetProperty("paths");
        paths.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("/pets", "/pets/{id}", "/login", "/graphql");
        paths.GetProperty("/pets").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("get", "post");

        var getPet = paths.GetProperty("/pets/{id}").GetProperty("get");
        getPet.GetProperty("parameters").EnumerateArray()
            .Should().Contain(p => p.GetProperty("name").GetString() == "id" && p.GetProperty("in").GetString() == "path");

        root.GetProperty("security")[0].EnumerateObject().Single().Name.Should().Be("bearerAuth");
        root.GetProperty("components").GetProperty("securitySchemes").EnumerateObject().Select(s => s.Name)
            .Should().BeEquivalentTo("bearerAuth", "basicAuth", "apiKeyAuth");
        paths.GetProperty("/pets").GetProperty("post").GetProperty("security")[0].EnumerateObject().Single().Name
            .Should().Be("basicAuth");
    }

    [Fact]
    public void Folders_become_tags()
    {
        using var doc = JsonDocument.Parse(Export());
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString());
        tags.Should().BeEquivalentTo("Pets", "Pets / Admin");
        doc.RootElement.GetProperty("paths").GetProperty("/pets/{id}").GetProperty("get").GetProperty("tags")[0]
            .GetString().Should().Be("Pets");
    }

    [Fact]
    public void Round_trips_through_OpenApiImporter()
    {
        var c = OpenApiImporter.ImportFromString(Export());
        c.Name.Should().Be("Pet Store");
        c.Variables.Should().Contain(v => v.Name == "baseUrl" && v.Value == "https://api.example.com/v1");
        c.Auth!.Type.Should().Be(AuthType.Bearer);
        c.Folders.Select(f => f.Name).Should().BeEquivalentTo("Pets", "Pets / Admin");

        var all = ExportFixtures.AllRequests(c);
        all.Select(r => r.Name).Should().BeEquivalentTo("List pets", "Get pet", "Create pet", "Login", "Pets query");

        var get = all.Single(r => r.Name == "Get pet");
        get.Method.Should().Be("GET");
        get.Url.Should().Be("{{baseUrl}}/pets/{id}");
        get.PathParams.Should().ContainSingle(p => p.Name == "id");

        all.Single(r => r.Name == "List pets").Params.Should().ContainSingle(p => p.Name == "limit");

        var create = all.Single(r => r.Name == "Create pet");
        create.Body.Mode.Should().Be(BodyMode.Json);
        create.Body.Content.Should().Contain("\"name\"").And.Contain("\"age\"");
        create.Auth!.Type.Should().Be(AuthType.Basic);

        all.Single(r => r.Name == "Login").Body.FormData.Select(p => p.Name).Should().Equal("user", "pass");
    }

    [Fact]
    public void Yaml_output_reimports()
    {
        var yaml = Export(yaml: true);
        yaml.Should().StartWith("openapi: 3.0");
        ExportFixtures.AllRequests(OpenApiImporter.ImportFromString(yaml)).Should().HaveCount(5);
    }

    [Fact]
    public void Warns_about_dropped_scripts_and_grpc()
    {
        var warnings = new List<string>();
        Export(warnings: warnings);
        warnings.Should().Contain(w => w.Contains("scripts, tests and environments"));
        warnings.Should().Contain(w => w.Contains("Watch"));
    }

    [Theory]
    [InlineData("{{baseUrl}}/pets/:id?x=1", "{{baseUrl}}", "/pets/{id}")]
    [InlineData("https://h.example.com:8080/a/{{v}}/b", "https://h.example.com:8080", "/a/{v}/b")]
    [InlineData("/relative", "", "/relative")]
    [InlineData("{{baseUrl}}", "{{baseUrl}}", "/")]
    public void Splits_urls_into_server_and_path(string url, string prefix, string path)
    {
        var (p, rest) = OpenApiExporter.SplitPrefix(url);
        p.Should().Be(prefix);
        OpenApiExporter.ToOpenApiPath(rest).Path.Should().Be(path);
    }

    [Fact]
    public void Same_template_with_different_param_names_shares_one_path()
    {
        var c = new Collection
        {
            Name = "X",
            Requests =
            {
                new RequestItem { Name = "Get", Method = "GET", Url = "https://x.test/a/:id/b/:bid" },
                new RequestItem { Name = "Delete", Method = "DELETE", Url = "https://x.test/a/{{accountId}}/b/:billId", PathParams = { new KvPair("billId", "7") } },
            },
        };
        var json = ExportFixtures.Text(OpenApiExporter.Export(c, new ExportOptions(), new List<string>()));

        new Microsoft.OpenApi.Readers.OpenApiStringReader().Read(json, out var diag);
        diag.Errors.Should().BeEmpty();
        using var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("paths").EnumerateObject().Should().ContainSingle().Subject;
        path.Name.Should().Be("/a/{id}/b/{bid}");
        var bid = path.Value.GetProperty("delete").GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "bid");
        bid.GetProperty("example").GetString().Should().Be("7", "the request's own :billId value maps onto {bid} by position");
    }

    [Fact]
    public void Unresolvable_server_variables_get_a_non_empty_default()
    {
        var c = new Collection
        {
            Name = "X",
            Requests = { new RequestItem { Name = "Get", Method = "GET", Url = "https://{{tenant}}.x.test/items" } },
        };
        var json = ExportFixtures.Text(OpenApiExporter.Export(c, new ExportOptions(), new List<string>()));
        new Microsoft.OpenApi.Readers.OpenApiStringReader().Read(json, out var diag);
        diag.Errors.Should().BeEmpty();
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("servers")[0];
        server.GetProperty("url").GetString().Should().Be("https://{tenant}.x.test");
        server.GetProperty("variables").GetProperty("tenant").GetProperty("default").GetString().Should().Be("tenant");
    }

    [Fact]
    public void Requests_folded_into_examples_are_reported()
    {
        var c = new Collection
        {
            Name = "X",
            Requests =
            {
                new RequestItem { Name = "Create A", Method = "POST", Url = "https://x.test/items", Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"a\":1}" } },
                new RequestItem { Name = "Create B", Method = "POST", Url = "https://x.test/items", Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"b\":2}" } },
            },
        };
        var warnings = new List<string>();
        var doc = OpenApiExporter.BuildDocument(c, warnings);
        doc.Paths["/items"].Operations[Microsoft.OpenApi.Models.OperationType.Post]
            .RequestBody.Content["application/json"].Examples.Keys.Should().BeEquivalentTo("Create A", "Create B");
        warnings.Should().ContainSingle(w => w.Contains("1 request(s) share a method and path"));
    }

    [Fact]
    public void Unquoted_placeholders_in_json_bodies_still_produce_a_schema()
    {
        var c = new Collection
        {
            Name = "X",
            Requests =
            {
                new RequestItem
                {
                    Name = "Upsert",
                    Method = "PUT",
                    Url = "https://x.test/items",
                    Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"id\": {{itemId}}, \"tags\": [\"a\"]}" },
                },
            },
        };
        var doc = OpenApiExporter.BuildDocument(c, new List<string>());
        var schema = doc.Paths["/items"].Operations[Microsoft.OpenApi.Models.OperationType.Put]
            .RequestBody.Content["application/json"].Schema;
        schema.Properties.Keys.Should().BeEquivalentTo("id", "tags");
        schema.Properties["tags"].Type.Should().Be("array");
    }
}
