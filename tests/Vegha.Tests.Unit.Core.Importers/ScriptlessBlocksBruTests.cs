using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Round-trips the scriptless-testing blocks — <c>assert</c>, <c>vars:post-response[:env|:global]</c>
/// and <c>chain:pre-request</c> — through the request and folder/collection emitters + loaders.</summary>
public class ScriptlessBlocksBruTests
{
    private static RequestItem RoundTrip(RequestItem item) =>
        BruToRequestConverter.Convert(BruParser.Parse(BruEmitter.Emit(item)));

    [Fact]
    public void Request_blocks_round_trip_including_awkward_keys()
    {
        var item = new RequestItem
        {
            Name = "Login",
            Method = "POST",
            Url = "https://api.test/login",
            PostResponseVars = { new KvPair("token", "res.body.access_token") },
            PostResponseEnvVars = { new KvPair("refresh", "res.headers['x-refresh'] | setIfValue") },
            PostResponseGlobalVars = { new KvPair("uid", "res.body.user.id", enabled: false) },
            Assertions =
            {
                new KvPair("res.status", "eq 200"),
                new KvPair("res.body.items | filter(status=active) | prop(\"id\")", "isNumber"),
                new KvPair("res.headers['content-type']", "contains json"),
                new KvPair("res.body.user", "matchesSchema {\"type\":\"object\"}"),
            },
            ChainLinks =
            {
                new KvPair("auth/login.bru", "when-empty token"),
                new KvPair("seed data.bru", "when {{role}} = admin"),
            },
        };

        var rt = RoundTrip(item);

        rt.PostResponseVars.Should().BeEquivalentTo(item.PostResponseVars);
        rt.PostResponseEnvVars.Should().BeEquivalentTo(item.PostResponseEnvVars);
        rt.PostResponseGlobalVars.Should().BeEquivalentTo(item.PostResponseGlobalVars);
        rt.Assertions.Should().BeEquivalentTo(item.Assertions, o => o.WithStrictOrdering());
        rt.ChainLinks.Should().BeEquivalentTo(item.ChainLinks, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Empty_blocks_are_not_emitted()
    {
        var bru = BruEmitter.Emit(new RequestItem { Name = "x", Url = "http://a" });
        bru.Should().NotContain("assert {").And.NotContain("chain:pre-request").And.NotContain("vars:post-response");
    }

    [Fact]
    public void Bruno_assert_block_is_read()
    {
        var bru = """
            meta {
              name: Get user
              type: http
              seq: 1
            }

            get {
              url: {{base}}/users/1
              body: none
              auth: none
            }

            assert {
              res.status: eq 200
              ~res.body.id: isNumber
            }
            """;
        var item = BruToRequestConverter.Convert(BruParser.Parse(bru));
        item.Assertions.Should().BeEquivalentTo(new[]
        {
            new KvPair("res.status", "eq 200"),
            new KvPair("res.body.id", "isNumber", enabled: false),
        });
    }

    [Fact]
    public void Folder_and_collection_blocks_round_trip_through_loader()
    {
        var root = Directory.CreateTempSubdirectory("vegha-scriptless-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "collection.bru"), BruMetaEmitter.EmitCollection(new Collection
            {
                Name = "C",
                Assertions = { new KvPair("res.status", "lt 500") },
                ChainLinks = { new KvPair("auth/login.bru", "when-stale 30m") },
            }));
            var folder = Path.Combine(root, "users");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "folder.bru"), BruMetaEmitter.EmitFolder(new Folder
            {
                Name = "users",
                Assertions = { new KvPair("res.responseTime", "lt 2000") },
            }));

            var c = CollectionLoader.Load(root);
            c.Assertions.Should().ContainSingle().Which.Should().Be(new KvPair("res.status", "lt 500"));
            c.ChainLinks.Should().ContainSingle().Which.Value.Should().Be("when-stale 30m");
            c.Folders.Single().Assertions.Should().ContainSingle().Which.Name.Should().Be("res.responseTime");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("", ChainRunMode.Always, "")]
    [InlineData("always", ChainRunMode.Always, "")]
    [InlineData("when-empty token", ChainRunMode.WhenEmpty, "token")]
    [InlineData("when-stale 2h", ChainRunMode.WhenStale, "2h")]
    [InlineData("when {{role}} = admin", ChainRunMode.When, "{{role}} = admin")]
    public void Chain_link_spec_parses(string raw, ChainRunMode mode, string arg)
    {
        var spec = ChainLinkSpec.Parse(raw);
        spec.Mode.Should().Be(mode);
        spec.Argument.Should().Be(arg);
        ChainLinkSpec.Parse(spec.ToStorage()).Should().Be(spec);
    }

    [Fact]
    public void Chain_link_max_age_units()
    {
        ChainLinkSpec.Parse("when-stale 30s").MaxAge.Should().Be(TimeSpan.FromSeconds(30));
        ChainLinkSpec.Parse("when-stale 2h").MaxAge.Should().Be(TimeSpan.FromHours(2));
        ChainLinkSpec.Parse("when-stale 15").MaxAge.Should().Be(TimeSpan.FromMinutes(15));
        ChainLinkSpec.Parse("when-stale bogus").MaxAge.Should().Be(TimeSpan.FromMinutes(10));
    }
}
