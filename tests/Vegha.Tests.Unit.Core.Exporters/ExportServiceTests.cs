using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.FileFormat;
using Vegha.Core.Importers;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.Exporters;

public class ExportServiceTests
{
    private static ExportSource[] Sources(params Collection[] c) => c.Select(x => new ExportSource(x)).ToArray();

    [Theory]
    [InlineData(ExportFormat.OpenApi30, "Pet Store.openapi.json")]
    [InlineData(ExportFormat.InsomniaV4, "Pet Store.insomnia.json")]
    public void Single_collection_exports_one_file(ExportFormat format, string fileName)
    {
        var result = ExportService.Export(Sources(ExportFixtures.PetStore()), format, new ExportOptions());
        result.IsZip.Should().BeFalse();
        result.SuggestedFileName.Should().Be(fileName);
        result.CollectionCount.Should().Be(1);
    }

    [Fact]
    public void Single_postman_collection_is_a_plain_file_without_environments()
    {
        var result = ExportService.Export(Sources(ExportFixtures.PetStore()), ExportFormat.PostmanV21,
            new ExportOptions(IncludeEnvironments: false));
        result.IsZip.Should().BeFalse();
        result.SuggestedFileName.Should().Be("Pet Store.postman_collection.json");
    }

    [Fact]
    public void Single_postman_collection_with_environments_is_a_zip_that_reimports()
    {
        var result = ExportService.Export(Sources(ExportFixtures.PetStore()), ExportFormat.PostmanV21, new ExportOptions());
        result.IsZip.Should().BeTrue();
        var entries = ExportFixtures.Unzip(result.Content);
        entries.Keys.Should().BeEquivalentTo("Pet Store.postman_collection.json", "environments/Prod.postman_environment.json");

        // The existing ZIP import path finds the collection inside.
        var import = ImportPipeline.DetectAndImport(result.Content, result.SuggestedFileName);
        import.Success.Should().BeTrue(import.Error);
        import.Collection!.Name.Should().Be("Pet Store");
    }

    [Fact]
    public void Workspace_export_zips_collections_envs_and_scripts()
    {
        var workspace = new ExportWorkspace("Team", new WorkspaceModel
        {
            Environments = new List<DomainEnv>
            {
                new() { Name = "Global", Variables = { new KvPair("key", "v"), new KvPair("pw", "hidden") }, SecretVariables = { "pw" } },
            },
            PreRequestScript = "console.log('ws');",
        });
        var other = ExportFixtures.PetStore() with { Name = "Pet Store" }; // same name → de-duplicated

        var result = ExportService.Export(Sources(ExportFixtures.PetStore(), other), ExportFormat.PostmanV21,
            new ExportOptions(), workspace);

        result.SuggestedFileName.Should().Be("Team-postman.zip");
        result.CollectionCount.Should().Be(2);
        var entries = ExportFixtures.Unzip(result.Content);
        entries.Keys.Should().BeEquivalentTo(
            "collections/Pet Store.postman_collection.json",
            "collections/Pet Store-2.postman_collection.json",
            "environments/Pet Store/Prod.postman_environment.json",
            "environments/Pet Store-2/Prod.postman_environment.json",
            "environments/Global.postman_environment.json",
            "scripts/pre-request.js");
        ExportFixtures.Text(entries["environments/Global.postman_environment.json"]).Should().NotContain("hidden");
        ExportFixtures.Text(entries["scripts/pre-request.js"]).Should().Be("console.log('ws');");
    }

    [Fact]
    public void Workspace_bruno_export_nests_each_collection_tree()
    {
        var result = ExportService.Export(Sources(ExportFixtures.PetStore()), ExportFormat.Bruno, new ExportOptions(),
            new ExportWorkspace("Team", WorkspaceModel.Empty));
        ExportFixtures.Unzip(result.Content).Keys.Should()
            .Contain("collections/Pet Store/collection.bru")
            .And.Contain("collections/Pet Store/environments/Prod.bru");
    }

    [Fact]
    public void Excluding_environments_leaves_them_out_everywhere()
    {
        var result = ExportService.Export(Sources(ExportFixtures.PetStore()), ExportFormat.Bruno,
            new ExportOptions(IncludeEnvironments: false));
        ExportFixtures.Unzip(result.Content).Keys.Should().NotContain(k => k.Contains("environments/"));
    }

    [Fact]
    public void Collection_names_are_sanitized_for_file_names()
    {
        var c = new Collection { Name = "a/b:c?" };
        ExportService.Export(Sources(c), ExportFormat.InsomniaV4, new ExportOptions())
            .SuggestedFileName.Should().NotContainAny("/", ":", "?");
    }

    [Fact]
    public void Redact_blanks_only_secret_values()
    {
        var env = ExportFixtures.PetStore().Environments[0];
        var redacted = EnvironmentExportHelper.Redact(env, includeSecrets: false);
        redacted.Variables.Single(v => v.Name == "token").Value.Should().BeEmpty();
        redacted.Variables.Single(v => v.Name == "baseUrl").Value.Should().NotBeEmpty();
        redacted.SecretVariables.Should().Equal("token");
        EnvironmentExportHelper.Redact(env, includeSecrets: true).Should().BeSameAs(env);
    }
}
