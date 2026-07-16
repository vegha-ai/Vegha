using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class CollectionLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public CollectionLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Vegha-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string MinimalRequest(string name, string url, int seq = 1) => $$"""
        meta {
          name: {{name}}
          type: http
          seq: {{seq}}
        }

        get {
          url: {{url}}
          body: none
          auth: none
        }
        """;

    [Fact]
    public void Load_ReturnsCollectionNamedAfterDirectory_WhenNoCollectionBru()
    {
        Write("ping.bru", MinimalRequest("ping", "https://example.com/ping"));

        var col = CollectionLoader.Load(_tempDir);

        col.Name.Should().Be(Path.GetFileName(_tempDir));
        col.Requests.Should().HaveCount(1);
        col.Requests[0].Name.Should().Be("ping");
        col.Requests[0].Url.Should().Be("https://example.com/ping");
    }

    [Fact]
    public void Load_UsesCollectionBruNameWhenPresent()
    {
        Write("collection.bru", """
            meta {
              name: My Acme Collection
            }
            """);
        Write("ping.bru", MinimalRequest("ping", "https://example.com/ping"));

        var col = CollectionLoader.Load(_tempDir);
        col.Name.Should().Be("My Acme Collection");
    }

    [Fact]
    public void Load_BuildsNestedFolders()
    {
        Write("ping.bru", MinimalRequest("ping", "https://example.com/ping"));
        Write("auth/login.bru", MinimalRequest("login", "https://example.com/login"));
        Write("auth/logout.bru", MinimalRequest("logout", "https://example.com/logout", seq: 2));
        Write("users/get.bru", MinimalRequest("get user", "https://example.com/users/1"));

        var col = CollectionLoader.Load(_tempDir);

        col.Requests.Should().HaveCount(1);
        col.Folders.Should().HaveCount(2);

        var auth = col.Folders.First(f => f.Name == "auth");
        auth.Requests.Select(r => r.Name).Should().Equal("login", "logout"); // sorted by seq

        var users = col.Folders.First(f => f.Name == "users");
        users.Requests.Should().ContainSingle(r => r.Name == "get user");
    }

    [Fact]
    public void Load_SortsRequestsBySequence()
    {
        Write("c.bru", MinimalRequest("c-third", "https://example.com/c", seq: 3));
        Write("a.bru", MinimalRequest("a-first", "https://example.com/a", seq: 1));
        Write("b.bru", MinimalRequest("b-second", "https://example.com/b", seq: 2));

        var col = CollectionLoader.Load(_tempDir);
        col.Requests.Select(r => r.Name).Should().Equal("a-first", "b-second", "c-third");
    }

    [Fact]
    public void Load_IgnoresHiddenAndBuildFolders()
    {
        Write(".git/HEAD", "fake");
        Write("node_modules/pkg/index.js", "//");
        Write("bin/Debug/x.dll", "//");
        Write("real.bru", MinimalRequest("real", "https://example.com/real"));

        var col = CollectionLoader.Load(_tempDir);
        col.Folders.Should().BeEmpty();
        col.Requests.Should().HaveCount(1);
    }

    [Fact]
    public void Load_SkipsMalformedBruFiles_WithoutCrashing()
    {
        Write("good.bru", MinimalRequest("good", "https://example.com/good"));
        Write("bad.bru", "this is not a valid bru file at all");

        var col = CollectionLoader.Load(_tempDir);
        col.Requests.Should().HaveCount(1);
        col.Requests[0].Name.Should().Be("good");
    }

    [Fact]
    public void Load_FallsBackToFileName_WhenMetaNameMissing()
    {
        Write("orphan.bru", """
            get {
              url: https://example.com
              body: none
              auth: none
            }
            """);

        var col = CollectionLoader.Load(_tempDir);
        col.Requests.Should().ContainSingle(r => r.Name == "orphan");
    }

    [Fact]
    public void Load_FolderBru_OverridesDirectoryName()
    {
        Write("api/folder.bru", """
            meta {
              name: API v2
            }
            """);
        Write("api/req.bru", MinimalRequest("req", "https://example.com"));

        var col = CollectionLoader.Load(_tempDir);
        col.Folders.Should().ContainSingle(f => f.Name == "API v2");
    }

    [Fact]
    public void Load_Throws_WhenDirectoryMissing()
    {
        var nope = Path.Combine(_tempDir, "does-not-exist");
        Action act = () => CollectionLoader.Load(nope);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Load_ReportsUnparseableFile_ViaOnLoadIssue()
    {
        Write("good.bru", MinimalRequest("good", "https://example.com"));
        Write("broken.bru", "meta {\n  name: broken\n}\n\nget {\n  url: https://example.com\n"); // unterminated block

        var issues = new List<(string File, string Error)>();
        var collection = CollectionLoader.Load(_tempDir, (file, err) => issues.Add((file, err)));

        collection.Requests.Should().ContainSingle(r => r.Name == "good");
        issues.Should().ContainSingle();
        issues[0].File.Should().EndWith("broken.bru");
        issues[0].Error.Should().Contain("Unterminated");
    }

    [Fact]
    public void Load_ReportsUnparseableFile_InNestedFolder()
    {
        Write("sub/good.bru", MinimalRequest("good", "https://example.com"));
        Write("sub/broken.bru", "not a bru file at all {{{");

        var issues = new List<(string File, string Error)>();
        var collection = CollectionLoader.Load(_tempDir, (file, err) => issues.Add((file, err)));

        collection.Folders.Should().ContainSingle();
        issues.Should().ContainSingle(i => i.File.EndsWith("broken.bru"));
    }

    [Fact]
    public void Load_WithoutCallback_StillSkipsUnparseableFileQuietly()
    {
        Write("good.bru", MinimalRequest("good", "https://example.com"));
        Write("broken.bru", "get {\n  url: x\n"); // unterminated

        var collection = CollectionLoader.Load(_tempDir);

        collection.Requests.Should().ContainSingle(r => r.Name == "good");
    }
}
