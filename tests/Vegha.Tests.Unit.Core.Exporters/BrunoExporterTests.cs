using FluentAssertions;
using Vegha.Core.Exporters;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Exporters;

public sealed class BrunoExporterTests : IDisposable
{
    private readonly string _source = ExportFixtures.NewTempDir();
    private readonly string _extract = ExportFixtures.NewTempDir();

    public void Dispose()
    {
        foreach (var d in new[] { _source, _extract })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    /// <summary>A realistic on-disk collection: the fixture written as .bru files, plus
    /// the side files a real collection folder carries.</summary>
    private void SeedSourceFolder()
    {
        BruCollectionWriter.Write(_source, ExportFixtures.PetStore());
        File.WriteAllText(Path.Combine(_source, "helpers.js"), "module.exports = {};");
        Directory.CreateDirectory(Path.Combine(_source, ".git"));
        File.WriteAllText(Path.Combine(_source, ".git", "config"), "[core]");
        Directory.CreateDirectory(Path.Combine(_source, ".secrets"));
        File.WriteAllText(Path.Combine(_source, ".secrets", "env-1.enc"), "encrypted");
        Directory.CreateDirectory(Path.Combine(_source, "environments"));
        File.WriteAllText(Path.Combine(_source, "environments", "Prod.env.json"), "{\"name\":\"Prod\"}");
    }

    private ExportResult Export(bool includeSecrets = false) =>
        ExportService.Export(
            new[] { new ExportSource(ExportFixtures.PetStore(), _source) },
            ExportFormat.Bruno,
            new ExportOptions(IncludeSecrets: includeSecrets));

    [Fact]
    public void Copies_the_collection_folder_and_skips_vcs_and_secret_sidecars()
    {
        SeedSourceFolder();
        var result = Export();
        result.IsZip.Should().BeTrue();
        result.SuggestedFileName.Should().Be("Pet Store.bruno.zip");

        var entries = ExportFixtures.Unzip(result.Content).Keys.ToList();
        entries.Should().Contain(new[]
        {
            "Pet Store/collection.bru",
            "Pet Store/List pets.bru",
            "Pet Store/Pets/folder.bru",
            "Pet Store/Pets/Get pet.bru",
            "Pet Store/helpers.js",
            "Pet Store/bruno.json",
            "Pet Store/environments/Prod.bru",
        });
        entries.Should().NotContain(e => e.Contains("/.git/") || e.Contains("/.secrets/") || e.EndsWith(".env.json"));
    }

    [Fact]
    public void Exported_zip_reimports_as_the_same_collection_with_secrets_blank()
    {
        SeedSourceFolder();
        var import = ImportPipeline.DetectAndImport(Export().Content, "Pet Store.bruno.zip");
        import.Success.Should().BeTrue(import.Error);
        import.FolderPath.Should().NotBeNull();

        var c = CollectionLoader.Load(import.FolderPath!);
        ExportFixtures.AllRequests(c).Select(r => r.Name)
            .Should().BeEquivalentTo("List pets", "Get pet", "Create pet", "Login", "Pets query", "Watch");

        var prod = c.Environments.Should().ContainSingle().Subject;
        prod.SecretVariables.Should().Contain("token");
        prod.Variables.Should().NotContain(v => v.Value == ExportFixtures.SecretValue);
    }

    [Fact]
    public void Includes_secret_values_when_opted_in()
    {
        SeedSourceFolder();
        var env = ExportFixtures.Text(ExportFixtures.Unzip(Export(includeSecrets: true).Content)["Pet Store/environments/Prod.bru"]);
        env.Should().Contain("token: " + ExportFixtures.SecretValue);
        env.Should().NotContain("vars:secret");
    }

    [Fact]
    public void In_memory_collection_is_serialized_when_there_is_no_source_folder()
    {
        var result = ExportService.Export(
            new[] { new ExportSource(ExportFixtures.PetStore()) }, ExportFormat.Bruno, new ExportOptions());
        ExportFixtures.Unzip(result.Content).Keys.Should().Contain("Pet Store/collection.bru").And.Contain("Pet Store/bruno.json");
    }
}
