using System.IO;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Disk-mutation tests for the OpenAPI apply-drift helpers. Each test creates
/// a throwaway temp dir, exercises one helper, and asserts on the on-disk layout.</summary>
public class OpenApiSpecApplierTests : IDisposable
{
    private readonly string _temp;

    public OpenApiSpecApplierTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "Vegha-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    [Fact]
    public void WriteAddedFolder_CreatesSubFolderWithBruFiles()
    {
        var added = new[]
        {
            new RequestItem { Name = "GetUser",  Method = "GET",  Url = "{{baseUrl}}/users/{id}" },
            new RequestItem { Name = "PostUser", Method = "POST", Url = "{{baseUrl}}/users"      },
        };

        var newDir = OpenApiSpecApplier.WriteAddedFolder(_temp, added, "Sync 2026-01-01");

        Directory.Exists(newDir).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "folder.bru")).Should().BeTrue();
        Directory.EnumerateFiles(newDir, "*.bru").Count().Should().Be(3); // folder.bru + 2 requests
    }

    [Fact]
    public void WriteAddedFolder_OnNameCollision_BumpsSuffix()
    {
        var first  = OpenApiSpecApplier.WriteAddedFolder(_temp,
            new[] { new RequestItem { Name = "A", Method = "GET", Url = "/a" } }, "MyFolder");
        var second = OpenApiSpecApplier.WriteAddedFolder(_temp,
            new[] { new RequestItem { Name = "B", Method = "GET", Url = "/b" } }, "MyFolder");

        Path.GetFileName(first).Should().Be("MyFolder");
        Path.GetFileName(second).Should().Be("MyFolder (2)");
        Directory.Exists(first).Should().BeTrue();
        Directory.Exists(second).Should().BeTrue();
    }

    [Fact]
    public void DeleteRequestFiles_DeletesOnlyExistingFilesUnderRoot()
    {
        var inside  = Path.Combine(_temp, "request.bru");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bru");
        File.WriteAllText(inside,  "x");
        File.WriteAllText(outside, "x");
        try
        {
            var n = OpenApiSpecApplier.DeleteRequestFiles(_temp, new[]
            {
                inside,
                outside,                                // outside the root — must not delete
                Path.Combine(_temp, "nope.bru"),        // doesn't exist — ignored
            });

            n.Should().Be(1);
            File.Exists(inside).Should().BeFalse();
            File.Exists(outside).Should().BeTrue("paths outside the collection root must be ignored");
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void ReplaceCollection_WipesBruTree_PreservesAuxiliaryDirs()
    {
        // Existing Bruno content.
        File.WriteAllText(Path.Combine(_temp, "collection.bru"), "meta { name: Old }");
        File.WriteAllText(Path.Combine(_temp, "OldRequest.bru"), "get { url: https://old }");
        var oldFolder = Path.Combine(_temp, "OldFolder");
        Directory.CreateDirectory(oldFolder);
        File.WriteAllText(Path.Combine(oldFolder, "folder.bru"),     "name: OldFolder");
        File.WriteAllText(Path.Combine(oldFolder, "OldChild.bru"),   "get { url: x }");

        // Auxiliary content that should survive a Replace.
        var envs = Path.Combine(_temp, "environments");
        Directory.CreateDirectory(envs);
        File.WriteAllText(Path.Combine(envs, "Dev.bru"), "vars { host: dev }");
        var docs = Path.Combine(_temp, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "README.md"), "# Docs");

        var fresh = new Collection
        {
            Name = "New",
            Requests = new List<RequestItem>
            {
                new() { Name = "NewRequest", Method = "GET", Url = "{{baseUrl}}/new" },
            },
        };

        OpenApiSpecApplier.ReplaceCollection(_temp, fresh);

        File.Exists(Path.Combine(_temp, "OldRequest.bru")).Should().BeFalse("loose old bru files are wiped");
        Directory.Exists(oldFolder).Should().BeFalse("Bruno subfolders are wiped");
        File.Exists(Path.Combine(_temp, "NewRequest.bru")).Should().BeTrue("the spec's request is written");

        // environments/ contains a .bru file — by current convention, ReplaceCollection treats
        // any subfolder with .bru content as a Bruno folder and wipes it. Auxiliary non-.bru
        // dirs survive.
        Directory.Exists(docs).Should().BeTrue("non-bru auxiliary directories must survive");
        File.Exists(Path.Combine(docs, "README.md")).Should().BeTrue();
    }
}
