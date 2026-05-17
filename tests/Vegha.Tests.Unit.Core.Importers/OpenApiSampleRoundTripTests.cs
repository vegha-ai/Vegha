using System.IO;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>End-to-end import of the bundled <c>samples/openapi/*</c> specs — the public
/// Swagger Petstore OpenAPI 3.0 document, in both JSON and YAML form. Sanity-checks that a
/// real-world spec survives format detection + import via the pipeline.</summary>
public class OpenApiSampleRoundTripTests
{
    private static string SamplesDir()
    {
        // Walk up from the test-binary directory until we find the repo's `samples` folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "openapi")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("samples/openapi not found above test binary.");
        return Path.Combine(dir.FullName, "samples", "openapi");
    }

    [Theory]
    [InlineData("petstore.json")]
    [InlineData("petstore.yaml")]
    public void Sample_ImportsViaPipeline(string filename)
    {
        var path = Path.Combine(SamplesDir(), filename);
        File.Exists(path).Should().BeTrue($"sample {filename} should be present");

        var result = ImportPipeline.DetectAndImportPath(path);

        result.Success.Should().BeTrue($"pipeline import error: {result.Error}");
        result.Collection.Should().NotBeNull();
        result.FormatLabel.Should().StartWith("OpenAPI / Swagger");

        var c = result.Collection!;
        // Petstore groups its operations under the pet / store / user tags.
        c.Folders.Count.Should().BeGreaterThan(0, "the sample organizes operations under tags");
        (c.Requests.Count + c.Folders.Sum(f => f.Requests.Count))
            .Should().BeGreaterThan(0, "the spec defines operations");
    }
}
