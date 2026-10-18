using System.Text.Json;
using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.Importers;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.Exporters;

public class InsomniaV4ExporterTests
{
    private static string Export(ExportOptions? options = null, List<string>? warnings = null, IEnumerable<DomainEnv>? extra = null) =>
        ExportFixtures.Text(InsomniaV4Exporter.Export(
            ExportFixtures.PetStore(), options ?? new ExportOptions(), warnings ?? new List<string>(), extra));

    [Fact]
    public void Emits_v4_export_envelope()
    {
        using var doc = JsonDocument.Parse(Export());
        doc.RootElement.GetProperty("_type").GetString().Should().Be("export");
        doc.RootElement.GetProperty("__export_format").GetInt32().Should().Be(4);
        var types = doc.RootElement.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("_type").GetString()).ToList();
        types.Should().Contain(new[] { "workspace", "environment", "request_group", "request" });
    }

    [Fact]
    public void Round_trips_through_InsomniaImporter()
    {
        var c = InsomniaImporter.ImportFromString(Export());
        c.Name.Should().Be("Pet Store");
        c.Folders.Select(f => f.Name).Should().Equal("Pets", "Streams");
        c.Folders[0].Folders.Should().ContainSingle(f => f.Name == "Admin");

        var all = ExportFixtures.AllRequests(c);
        all.Select(r => r.Name).Should().BeEquivalentTo("List pets", "Get pet", "Create pet", "Login", "Pets query");

        var list = all.Single(r => r.Name == "List pets");
        list.Url.Should().Be("{{baseUrl}}/pets", "query params live in parameters[], not the URL");
        list.Params.Should().ContainSingle(p => p.Name == "limit" && p.Value == "10");

        all.Single(r => r.Name == "Get pet").Url.Should().Be("{{baseUrl}}/pets/42", "path params are substituted");

        var create = all.Single(r => r.Name == "Create pet");
        create.Body.Mode.Should().Be(BodyMode.Json);
        create.Body.Content.Should().Be("{\"name\":\"Rex\",\"age\":3}");

        all.Single(r => r.Name == "Login").Body.FormData.Select(p => p.Name).Should().Equal("user", "pass");

        var gql = all.Single(r => r.Name == "Pets query");
        gql.Kind.Should().Be(RequestKind.GraphQL);
        gql.Body.GraphQLQuery.Should().Be("{ pets { id } }");
    }

    [Fact]
    public void Requests_carry_inherited_headers_and_auth()
    {
        var all = ExportFixtures.AllRequests(InsomniaImporter.ImportFromString(Export()));
        var get = all.Single(r => r.Name == "Get pet");
        get.Headers.Select(h => h.Name).Should().Contain(new[] { "X-Client", "X-Folder" });
        get.Auth!.Type.Should().Be(AuthType.Bearer);
        get.Auth.Parameters["token"].Should().Be("{{token}}", "{{ _.token }} normalizes back");

        all.Single(r => r.Name == "Create pet").Auth!.Type.Should().Be(AuthType.Basic, "request override wins");
    }

    [Fact]
    public void Uses_nunjucks_variable_syntax()
    {
        var json = Export();
        json.Should().Contain("{{ _.baseUrl }}/pets");
        json.Should().NotContain("\"{{baseUrl}}");
    }

    [Fact]
    public void Environments_become_sub_environments_with_secrets_redacted()
    {
        using var doc = JsonDocument.Parse(Export(extra: new[] { new DomainEnv { Name = "Global", Variables = { new KvPair("g", "1") } } }));
        var envs = doc.RootElement.GetProperty("resources").EnumerateArray()
            .Where(r => r.GetProperty("_type").GetString() == "environment").ToList();
        envs.Select(e => e.GetProperty("name").GetString()).Should().Equal("Base Environment", "Prod", "Global");

        envs[0].GetProperty("data").GetProperty("baseUrl").GetString().Should().Be("https://api.example.com/v1");
        envs[1].GetProperty("data").GetProperty("token").GetString().Should().BeEmpty();
        envs[1].GetProperty("parentId").GetString().Should().Be(envs[0].GetProperty("_id").GetString());
    }

    [Fact]
    public void Ids_are_deterministic()
    {
        string Ids(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return string.Join(",", doc.RootElement.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("_id").GetString()));
        }
        Ids(Export()).Should().Be(Ids(Export()));
    }

    [Fact]
    public void Warns_about_scripts_and_grpc()
    {
        var warnings = new List<string>();
        Export(warnings: warnings);
        warnings.Should().Contain(w => w.Contains("scripts"));
        warnings.Should().Contain(w => w.Contains("Watch"));
    }
}
