using Vegha.Integrations.Secrets;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Secrets;

public class SecretRegistryTests
{
    private sealed class FakeProvider : ISecretProvider
    {
        public string Name { get; }
        public int Calls { get; private set; }
        public Func<string, string?, string?> Resolver { get; }

        public FakeProvider(string name, Func<string, string?, string?> resolver)
        {
            Name = name;
            Resolver = resolver;
        }

        public Task<string?> GetSecretAsync(string path, string? field, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Resolver(path, field));
        }
    }

    [Fact]
    public async Task ResolveAsync_DispatchesToRegisteredProvider()
    {
        var provider = new FakeProvider("vault", (p, f) => "value-of-" + (f ?? "(scalar)"));
        var registry = new SecretRegistry();
        registry.Register(provider);

        var resolved = await registry.ResolveAsync("secret://vault/kv/data/x#k");
        resolved.Should().Be("value-of-k");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenProviderNotRegistered()
    {
        var registry = new SecretRegistry();
        var resolved = await registry.ResolveAsync("secret://aws/x");
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_CachesByCanonicalUri()
    {
        var provider = new FakeProvider("vault", (_, _) => "cached");
        var registry = new SecretRegistry { CacheTtl = TimeSpan.FromSeconds(60) };
        registry.Register(provider);

        await registry.ResolveAsync("secret://vault/path#k");
        await registry.ResolveAsync("secret://vault/path#k");
        await registry.ResolveAsync("secret://vault/path#k");

        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_DifferentFields_AreCachedSeparately()
    {
        var provider = new FakeProvider("vault", (p, f) => "value-" + f);
        var registry = new SecretRegistry();
        registry.Register(provider);

        var a = await registry.ResolveAsync("secret://vault/path#a");
        var b = await registry.ResolveAsync("secret://vault/path#b");

        a.Should().Be("value-a");
        b.Should().Be("value-b");
        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateCache_ForcesRefetch()
    {
        var provider = new FakeProvider("vault", (_, _) => "v");
        var registry = new SecretRegistry();
        registry.Register(provider);

        await registry.ResolveAsync("secret://vault/x#y");
        registry.InvalidateCache();
        await registry.ResolveAsync("secret://vault/x#y");

        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Register_WithExplicitName_KeysByThatName()
    {
        // The adapter's own Name is "vault", but it is registered under a user config name —
        // so the URI targets "prod-vault", letting several vault-type providers coexist.
        var provider = new FakeProvider("vault", (p, _) => "resolved-" + p);
        var registry = new SecretRegistry();
        registry.Register("prod-vault", provider);

        (await registry.ResolveAsync("secret://prod-vault/db")).Should().Be("resolved-db");
        (await registry.ResolveAsync("secret://vault/db")).Should().BeNull();
        registry.ProviderNames.Should().ContainSingle().Which.Should().Be("prod-vault");
    }

    [Fact]
    public async Task ResolveSecretsAsync_ResolvesSecretUris_AndPassesOthersThrough()
    {
        var provider = new FakeProvider("azure", (p, _) => "secret-" + p);
        var registry = new SecretRegistry();
        registry.Register("prod", provider);

        var resolved = await registry.ResolveSecretsAsync(new Dictionary<string, string>
        {
            ["host"] = "api.example.com",
            ["apiKey"] = "secret://prod/my-key",
        });

        resolved["host"].Should().Be("api.example.com");
        resolved["apiKey"].Should().Be("secret-my-key");
    }

    [Fact]
    public async Task ResolveSecretsAsync_UnresolvableUri_KeepsLiteralText()
    {
        var registry = new SecretRegistry();

        var resolved = await registry.ResolveSecretsAsync(new Dictionary<string, string>
        {
            ["apiKey"] = "secret://missing/key",
        });

        // No provider registered — the URI stays literal rather than going silently blank.
        resolved["apiKey"].Should().Be("secret://missing/key");
    }
}
