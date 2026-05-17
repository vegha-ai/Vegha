using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class RequestCompositionTests
{
    [Fact]
    public void Headers_MergedAcrossLayers_LastWinsOnSameKey()
    {
        var collection = new Collection
        {
            Name = "C",
            Headers = new List<KvPair> { new("X-Trace", "from-collection"), new("X-Coll", "1") }
        };
        var folder = new Folder
        {
            Name = "F",
            Headers = new List<KvPair> { new("X-Trace", "from-folder"), new("X-Folder", "2") }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Headers = new List<KvPair> { new("X-Trace", "from-request"), new("X-Req", "3") }
        };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);

        var byName = composed.Headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
        byName["X-Trace"].Should().Be("from-request"); // request wins
        byName["X-Coll"].Should().Be("1");
        byName["X-Folder"].Should().Be("2");
        byName["X-Req"].Should().Be("3");
    }

    [Fact]
    public void Headers_DisabledRowInLowerLayer_RemovesEarlierEntry()
    {
        var collection = new Collection
        {
            Name = "C",
            Headers = new List<KvPair> { new("X-Trace", "from-collection") }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Headers = new List<KvPair> { new("X-Trace", "x", enabled: false) }
        };

        var composed = RequestComposition.Compose(collection, Array.Empty<Folder>(), request);

        composed.Headers.Should().NotContain(h => h.Name.Equals("X-Trace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Auth_RequestInherit_FallsThroughToFolder()
    {
        var collection = new Collection { Name = "C" };
        var folder = new Folder
        {
            Name = "F",
            Auth = new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new Dictionary<string, string> { ["token"] = "FOLDERTOKEN" }
            }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Auth = new AuthConfig { Type = AuthType.Inherit }
        };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);

        composed.Auth.Should().NotBeNull();
        composed.Auth!.Type.Should().Be(AuthType.Bearer);
        composed.Auth.Parameters["token"].Should().Be("FOLDERTOKEN");
    }

    [Fact]
    public void Auth_FolderInherit_FallsThroughToCollection()
    {
        var collection = new Collection
        {
            Name = "C",
            Auth = new AuthConfig
            {
                Type = AuthType.Basic,
                Parameters = new Dictionary<string, string> { ["username"] = "u", ["password"] = "p" }
            }
        };
        var folder = new Folder
        {
            Name = "F",
            Auth = new AuthConfig { Type = AuthType.Inherit }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Auth = new AuthConfig { Type = AuthType.Inherit }
        };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);
        composed.Auth!.Type.Should().Be(AuthType.Basic);
    }

    [Fact]
    public void Auth_RequestExplicitNone_StopsWalk()
    {
        var collection = new Collection
        {
            Name = "C",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "x" } }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Auth = new AuthConfig { Type = AuthType.None }
        };

        var composed = RequestComposition.Compose(collection, Array.Empty<Folder>(), request);
        composed.Auth!.Type.Should().Be(AuthType.None);
    }

    [Fact]
    public void Vars_LaterLayersOverrideEarlier()
    {
        var collection = new Collection
        {
            Name = "C",
            Variables = new List<KvPair> { new("baseUrl", "https://collection.test"), new("token", "C") }
        };
        var folder = new Folder
        {
            Name = "F",
            Variables = new List<KvPair> { new("token", "F") }
        };
        var request = new RequestItem
        {
            Name = "r", Url = "{{baseUrl}}",
            PreRequestVars = new List<KvPair> { new("token", "R") }
        };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);
        composed.Vars["baseUrl"].Should().Be("https://collection.test");
        composed.Vars["token"].Should().Be("R"); // request wins
    }

    [Fact]
    public void PreRequestScripts_ConcatenatedTopDown()
    {
        var collection = new Collection { Name = "C", PreRequestScript = "// from collection" };
        var folder = new Folder { Name = "F", PreRequestScript = "// from folder" };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            PreRequestScript = "// from request"
        };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);
        composed.PreRequestScript.Should().NotBeNull();
        var script = composed.PreRequestScript!;
        var collIdx = script.IndexOf("collection", StringComparison.Ordinal);
        var folderIdx = script.IndexOf("folder", StringComparison.Ordinal);
        var reqIdx = script.IndexOf("request", StringComparison.Ordinal);
        collIdx.Should().BeLessThan(folderIdx);
        folderIdx.Should().BeLessThan(reqIdx);
    }

    [Fact]
    public void Scripts_EmptyLayersSkipped()
    {
        var collection = new Collection { Name = "C", PreRequestScript = "// only collection" };
        var folder = new Folder { Name = "F", PreRequestScript = null };
        var request = new RequestItem { Name = "r", Url = "https://x.test", PreRequestScript = null };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);
        composed.PreRequestScript.Should().Be("// only collection");
    }

    [Fact]
    public void NoLayersHaveScripts_ReturnsNull()
    {
        var collection = new Collection { Name = "C" };
        var request = new RequestItem { Name = "r", Url = "https://x.test" };

        var composed = RequestComposition.Compose(collection, Array.Empty<Folder>(), request);
        composed.PreRequestScript.Should().BeNull();
        composed.TestsScript.Should().BeNull();
    }

    [Fact]
    public void Docs_NotInherited_RequestValueUsed()
    {
        var collection = new Collection { Name = "C", Docs = "collection docs" };
        var folder = new Folder { Name = "F", Docs = "folder docs" };
        var request = new RequestItem { Name = "r", Url = "https://x.test", Docs = "request docs" };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request);
        composed.Docs.Should().Be("request docs");
    }

    // ==================== ComposeWithSources (inheritance attribution) ====================

    [Fact]
    public void Sources_AuthInheritedFromInnermostFolder_WhenRequestUsesInherit()
    {
        var collection = new Collection
        {
            Name = "C",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "c" } },
        };
        var outer = new Folder
        {
            Name = "outer",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "o" } },
        };
        var inner = new Folder
        {
            Name = "inner",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "i" } },
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Auth = new AuthConfig { Type = AuthType.Inherit },
        };

        var (_, sources) = RequestComposition.ComposeWithSources(collection, new[] { outer, inner }, request);
        sources.Auth.Should().Contain("inner");
    }

    [Fact]
    public void Sources_AuthSourceIsNull_WhenRequestOwnsIt()
    {
        var collection = new Collection
        {
            Name = "C",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "c" } },
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "r" } },
        };

        var (_, sources) = RequestComposition.ComposeWithSources(collection, Array.Empty<Folder>(), request);
        sources.Auth.Should().BeNull();
    }

    [Fact]
    public void Sources_PreRequestScriptInheritedFromCollection_WhenRequestEmpty()
    {
        var collection = new Collection { Name = "C", PreRequestScript = "// from collection" };
        var folder = new Folder { Name = "F" };
        var request = new RequestItem { Name = "r", Url = "https://x.test" };

        var (_, sources) = RequestComposition.ComposeWithSources(collection, new[] { folder }, request);
        sources.PreRequestScript.Should().Contain("collection");
        sources.PreRequestScript.Should().Contain("C");
    }

    [Fact]
    public void Sources_PreRequestScriptSourceIsNull_WhenRequestOwnsIt()
    {
        var collection = new Collection { Name = "C", PreRequestScript = "// from collection" };
        var request = new RequestItem { Name = "r", Url = "https://x.test", PreRequestScript = "// from request" };

        var (_, sources) = RequestComposition.ComposeWithSources(collection, Array.Empty<Folder>(), request);
        sources.PreRequestScript.Should().BeNull();
    }

    [Fact]
    public void Sources_HeadersAttributeToContributingLayer_RequestOwnsItsOwn()
    {
        var collection = new Collection
        {
            Name = "C",
            Headers = new List<KvPair> { new("X-Coll", "1"), new("X-Trace", "from-c") },
        };
        var folder = new Folder
        {
            Name = "F",
            Headers = new List<KvPair> { new("X-Folder", "2"), new("X-Trace", "from-f") },
        };
        var request = new RequestItem
        {
            Name = "r", Url = "https://x.test",
            Headers = new List<KvPair> { new("X-Trace", "from-r"), new("X-Req", "3") },
        };

        var (_, sources) = RequestComposition.ComposeWithSources(collection, new[] { folder }, request);
        // Request owns X-Trace + X-Req — they're not in the inherited-source dict.
        sources.Headers.ContainsKey("X-Trace").Should().BeFalse();
        sources.Headers.ContainsKey("X-Req").Should().BeFalse();
        sources.Headers["X-Coll"].Should().Contain("collection");
        sources.Headers["X-Folder"].Should().Contain("folder");
    }
}
