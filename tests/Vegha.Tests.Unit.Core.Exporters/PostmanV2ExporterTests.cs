using System.Text.Json;
using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Exporters;

public class PostmanV2ExporterTests
{
    private static (Collection Reimported, string Json, List<string> Warnings) RoundTrip(ExportOptions? options = null)
    {
        var warnings = new List<string>();
        var bytes = PostmanV2Exporter.Export(ExportFixtures.PetStore(), options ?? new ExportOptions(), warnings);
        var json = ExportFixtures.Text(bytes);
        var reimported = PostmanV2Importer.ImportFromJson(json, new PostmanImportOptions(TranslateScripts: true));
        return (reimported, json, warnings);
    }

    [Fact]
    public void Emits_v21_schema_and_collection_metadata()
    {
        var (_, json, _) = RoundTrip();
        using var doc = JsonDocument.Parse(json);
        var info = doc.RootElement.GetProperty("info");
        info.GetProperty("schema").GetString().Should().Be(PostmanV2Exporter.SchemaUrl);
        info.GetProperty("name").GetString().Should().Be("Pet Store");
        doc.RootElement.GetProperty("auth").GetProperty("type").GetString().Should().Be("bearer");
        doc.RootElement.GetProperty("event")[0].GetProperty("listen").GetString().Should().Be("prerequest");
    }

    [Fact]
    public void Round_trips_tree_methods_urls_and_params()
    {
        var (c, _, _) = RoundTrip();
        c.Name.Should().Be("Pet Store");
        c.Variables.Should().ContainSingle(v => v.Name == "baseUrl" && v.Value == "https://api.example.com/v1");
        // The gRPC-only folder survives as an empty folder; its request is skipped.
        c.Folders.Select(f => f.Name).Should().Equal("Pets", "Streams");
        c.Folders[1].Requests.Should().BeEmpty();
        var all = ExportFixtures.AllRequests(c);
        all.Select(r => r.Name).Should().BeEquivalentTo("List pets", "Get pet", "Create pet", "Login", "Pets query");

        var list = all.Single(r => r.Name == "List pets");
        list.Method.Should().Be("GET");
        list.Url.Should().Be("{{baseUrl}}/pets?limit=10");
        list.Params.Should().ContainSingle(p => p.Name == "limit" && p.Value == "10");

        var get = all.Single(r => r.Name == "Get pet");
        get.PathParams.Should().ContainSingle(p => p.Name == "id" && p.Value == "42");
    }

    [Fact]
    public void Inlines_collection_and_folder_headers_into_requests()
    {
        var (c, _, warnings) = RoundTrip();
        var get = ExportFixtures.AllRequests(c).Single(r => r.Name == "Get pet");
        get.Headers.Select(h => h.Name).Should().Contain(new[] { "X-Client", "X-Folder" });
        warnings.Should().Contain(w => w.Contains("headers were copied"));
    }

    [Fact]
    public void Round_trips_bodies_and_auth()
    {
        var all = ExportFixtures.AllRequests(RoundTrip().Reimported);

        var create = all.Single(r => r.Name == "Create pet");
        create.Body.Mode.Should().Be(BodyMode.Json);
        create.Body.Content.Should().Be("{\"name\":\"Rex\",\"age\":3}");
        create.Auth!.Type.Should().Be(AuthType.Basic);
        create.Auth.Parameters["username"].Should().Be("user");

        var login = all.Single(r => r.Name == "Login");
        login.Body.Mode.Should().Be(BodyMode.FormUrlEncoded);
        login.Body.FormData.Select(p => p.Name).Should().Equal("user", "pass");
        login.Auth!.Type.Should().Be(AuthType.ApiKey);
        login.Auth.Parameters["key"].Should().Be("X-API-Key");
        login.Auth.Parameters["placement"].Should().Be("header");

        var gql = all.Single(r => r.Name == "Pets query");
        gql.Body.Mode.Should().Be(BodyMode.GraphQL);
        gql.Body.GraphQLQuery.Should().Be("{ pets { id } }");
    }

    [Fact]
    public void Scripts_translate_to_pm_and_back()
    {
        var (c, json, _) = RoundTrip();
        json.Should().Contain("pm.test('ok', () => pm.response.to.have.status(200));");
        ExportFixtures.AllRequests(c).Single(r => r.Name == "List pets").Tests
            .Should().Be("test('ok', () => expect(res.getStatus()).to.equal(200));");
    }

    [Fact]
    public void Skips_grpc_requests_with_a_warning()
    {
        var (_, json, warnings) = RoundTrip();
        json.Should().NotContain("Watch");
        warnings.Should().Contain(w => w.Contains("Grpc") && w.Contains("Watch"));
    }

    [Fact]
    public void Environment_redacts_secrets_by_default_and_keeps_secret_flag()
    {
        var env = ExportFixtures.PetStore().Environments[0];
        var json = ExportFixtures.Text(PostmanV2Exporter.ExportEnvironment(env, includeSecrets: false));
        json.Should().NotContain(ExportFixtures.SecretValue);

        var back = PostmanEnvironmentImporter.ImportFromString(json);
        back.Name.Should().Be("Prod");
        back.SecretVariables.Should().Equal("token");
        back.Variables.Single(v => v.Name == "token").Value.Should().BeEmpty();
        back.Variables.Single(v => v.Name == "baseUrl").Value.Should().Be("https://api.example.com/v1");
    }

    [Fact]
    public void Environment_includes_secret_values_when_opted_in()
    {
        var env = ExportFixtures.PetStore().Environments[0];
        var back = PostmanEnvironmentImporter.ImportFromString(
            ExportFixtures.Text(PostmanV2Exporter.ExportEnvironment(env, includeSecrets: true)));
        back.Variables.Single(v => v.Name == "token").Value.Should().Be(ExportFixtures.SecretValue);
    }
}
