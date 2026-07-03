using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class BruMetaEmitterTests : IDisposable
{
    private readonly string _root;

    public BruMetaEmitterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vegha-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void EmitCollection_RoundTrips_AllInheritanceFields()
    {
        var collection = new Collection
        {
            Name = "MyApi",
            Headers = new List<KvPair> { new("X-Trace", "abc"), new("X-Disabled", "v", false) },
            Variables = new List<KvPair> { new("baseUrl", "https://api.test"), new("env", "prod") },
            PreRequestScript = "bru.setVar('cid', 'C1');",
            TestsScript = "test('200', () => { expect(res.status).toBe(200); });",
            Docs = "Top-level docs.",
            Auth = new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new Dictionary<string, string> { ["token"] = "xyz" },
            },
        };

        var bruText = BruMetaEmitter.EmitCollection(collection);
        File.WriteAllText(Path.Combine(_root, "collection.bru"), bruText);
        // Need at least one request for CollectionLoader to surface anything other than empty.
        File.WriteAllText(Path.Combine(_root, "ping.bru"),
            "meta {\n  name: ping\n}\n\nget {\n  url: x\n}\n");

        var loaded = CollectionLoader.Load(_root);
        loaded.Name.Should().Be("MyApi");
        loaded.Headers.Should().ContainSingle(h => h.Name == "X-Trace" && h.Value == "abc" && h.Enabled);
        loaded.Headers.Should().Contain(h => h.Name == "X-Disabled" && !h.Enabled);
        loaded.Variables.Should().Contain(v => v.Name == "baseUrl" && v.Value == "https://api.test");
        loaded.PreRequestScript.Should().Contain("bru.setVar");
        loaded.TestsScript.Should().Contain("expect(res.status)");
        loaded.Docs.Should().Be("Top-level docs.");
        loaded.Auth!.Type.Should().Be(AuthType.Bearer);
        loaded.Auth.Parameters["token"].Should().Be("xyz");
    }

    [Fact]
    public void EmitFolder_RoundTrips_AllInheritanceFields()
    {
        var folderDir = Path.Combine(_root, "users");
        Directory.CreateDirectory(folderDir);
        var folder = new Folder
        {
            Name = "users",
            Headers = new List<KvPair> { new("X-Folder", "yes") },
            Variables = new List<KvPair> { new("region", "us-east-1") },
            PreRequestScript = "// folder pre-request",
            Docs = "Users folder",
        };
        File.WriteAllText(Path.Combine(folderDir, "folder.bru"), BruMetaEmitter.EmitFolder(folder));
        // The folder needs at least one request to be emitted (loader prunes empties).
        File.WriteAllText(Path.Combine(folderDir, "list.bru"),
            "meta {\n  name: list\n}\n\nget {\n  url: x\n}\n");

        var loaded = CollectionLoader.Load(_root);
        var loadedFolder = loaded.Folders.Single(f => f.Name == "users");
        loadedFolder.Headers.Should().ContainSingle(h => h.Name == "X-Folder" && h.Value == "yes");
        loadedFolder.Variables.Should().ContainSingle(v => v.Name == "region" && v.Value == "us-east-1");
        loadedFolder.PreRequestScript.Should().Contain("folder pre-request");
        loadedFolder.Docs.Should().Be("Users folder");
    }

    [Fact]
    public void EmitCollection_RoundTrips_PostResponseScript()
    {
        // Regression: NodePropertiesViewModel used to drop script:post-response on save.
        // The emitter/loader always round-tripped it; this pins the format so the VM fix holds.
        var collection = new Collection
        {
            Name = "PostApi",
            PreRequestScript = "bru.setVar('a', 1);",
            PostResponseScript = "bru.setEnvVar('token', res.body.token);",
        };
        File.WriteAllText(Path.Combine(_root, "collection.bru"), BruMetaEmitter.EmitCollection(collection));
        File.WriteAllText(Path.Combine(_root, "ping.bru"), "meta {\n  name: ping\n}\n\nget {\n  url: x\n}\n");

        var loaded = CollectionLoader.Load(_root);
        loaded.PreRequestScript.Should().Contain("bru.setVar");
        loaded.PostResponseScript.Should().Contain("bru.setEnvVar('token'");
    }

    [Fact]
    public void EmitCollection_RoundTrips_Presets()
    {
        var collection = new Collection
        {
            Name = "PresetApi",
            Presets = new RequestPresets { RequestType = "graphql", BaseUrl = "https://api.example.com/graphql" },
        };
        File.WriteAllText(Path.Combine(_root, "collection.bru"), BruMetaEmitter.EmitCollection(collection));
        File.WriteAllText(Path.Combine(_root, "ping.bru"), "meta {\n  name: ping\n}\n\nget {\n  url: x\n}\n");

        var loaded = CollectionLoader.Load(_root);
        loaded.Presets.Should().NotBeNull();
        loaded.Presets!.RequestType.Should().Be("graphql");
        loaded.Presets.BaseUrl.Should().Be("https://api.example.com/graphql");
    }

    [Fact]
    public void EmitCollection_OmitsEmptyPresets()
    {
        var c = new Collection { Name = "n", Presets = new RequestPresets() };
        BruMetaEmitter.EmitCollection(c).Should().NotContain("presets {");

        var c2 = new Collection { Name = "n" };
        BruMetaEmitter.EmitCollection(c2).Should().NotContain("presets {");
    }

    [Fact]
    public void EmitCollection_OmitsEmptyBlocks()
    {
        var bare = new Collection { Name = "Empty" };
        var text = BruMetaEmitter.EmitCollection(bare);
        text.Should().Contain("meta {");
        text.Should().NotContain("headers {");
        text.Should().NotContain("vars {");
        text.Should().NotContain("script:pre-request");
        text.Should().NotContain("auth:");
    }

    [Fact]
    public void EmitCollection_AuthInheritOrNone_OmitsAuthBlock()
    {
        var c = new Collection
        {
            Name = "n",
            Auth = new AuthConfig { Type = AuthType.Inherit },
        };
        BruMetaEmitter.EmitCollection(c).Should().NotContain("auth:");

        c = c with { Auth = new AuthConfig { Type = AuthType.None } };
        BruMetaEmitter.EmitCollection(c).Should().NotContain("auth:");
    }
}
