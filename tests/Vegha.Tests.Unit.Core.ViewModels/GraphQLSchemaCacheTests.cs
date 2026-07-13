using FluentAssertions;
using Vegha.App.ViewModels.Services;
using Vegha.Core.GraphQL.Schema;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class GraphQLSchemaCacheTests : IDisposable
{
    private const string Raw = """
    {
      "data": { "__schema": {
        "queryType": { "name": "Query" },
        "types": [
          { "kind": "OBJECT", "name": "Query",
            "fields": [ { "name": "ping", "type": { "kind": "SCALAR", "name": "String" }, "args": [] } ] }
        ]
      } }
    }
    """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vegha-tests", "gql-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task StoreThenGet_SameInstance_ServesFromMemory()
    {
        var cache = new GraphQLSchemaCache(_dir);
        var model = IntrospectionJsonReader.Parse(Raw);

        await cache.StoreAsync("https://api.acme.io/graphql", Raw, model);
        var got = await cache.TryGetAsync("https://api.acme.io/graphql");

        got.Should().BeSameAs(model);
    }

    [Fact]
    public async Task StoreThenGet_NewInstance_RehydratesFromDisk()
    {
        var model = IntrospectionJsonReader.Parse(Raw);
        await new GraphQLSchemaCache(_dir).StoreAsync("https://api.acme.io/graphql", Raw, model);

        var fresh = new GraphQLSchemaCache(_dir);
        var got = await fresh.TryGetAsync("https://api.acme.io/graphql");

        got.Should().NotBeNull();
        got!.QueryTypeName.Should().Be("Query");
        got.FindType("Query")!.Fields.Should().ContainSingle(f => f.Name == "ping");
    }

    [Fact]
    public async Task Miss_ReturnsNull()
    {
        var cache = new GraphQLSchemaCache(_dir);
        (await cache.TryGetAsync("https://unknown.example/graphql")).Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_DropsBothTiers()
    {
        var cache = new GraphQLSchemaCache(_dir);
        var model = IntrospectionJsonReader.Parse(Raw);
        await cache.StoreAsync("https://api.acme.io/graphql", Raw, model);

        cache.Invalidate("https://api.acme.io/graphql");

        (await cache.TryGetAsync("https://api.acme.io/graphql")).Should().BeNull();
        (await new GraphQLSchemaCache(_dir).TryGetAsync("https://api.acme.io/graphql")).Should().BeNull();
    }

    [Fact]
    public async Task CorruptDiskEntry_SilentlyMisses()
    {
        var cache = new GraphQLSchemaCache(_dir);
        var model = IntrospectionJsonReader.Parse(Raw);
        await cache.StoreAsync("https://api.acme.io/graphql", Raw, model);
        foreach (var f in Directory.GetFiles(_dir)) File.WriteAllText(f, "not json at all");

        var fresh = new GraphQLSchemaCache(_dir);
        (await fresh.TryGetAsync("https://api.acme.io/graphql")).Should().BeNull();
    }

    [Fact]
    public async Task UrlsAreCaseInsensitive_ButDistinctEndpointsDistinct()
    {
        var cache = new GraphQLSchemaCache(_dir);
        var model = IntrospectionJsonReader.Parse(Raw);
        await cache.StoreAsync("https://API.acme.io/graphql", Raw, model);

        (await cache.TryGetAsync("https://api.acme.io/graphql")).Should().NotBeNull();
        (await cache.TryGetAsync("https://api.other.io/graphql")).Should().BeNull();
    }
}
