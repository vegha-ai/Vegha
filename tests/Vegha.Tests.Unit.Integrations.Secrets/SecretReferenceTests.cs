using Vegha.Integrations.Secrets;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Secrets;

public class SecretReferenceTests
{
    [Theory]
    [InlineData("secret://vault/kv/data/acme/prod#api-key", "vault", "kv/data/acme/prod", "api-key")]
    [InlineData("secret://aws/my-secret", "aws", "my-secret", null)]
    [InlineData("secret://azure/db-password", "azure", "db-password", null)]
    [InlineData("secret://VAULT/upper#field", "vault", "upper", "field")]
    public void TryParse_RecognizesValidUris(string uri, string provider, string path, string? field)
    {
        var ok = SecretReference.TryParse(uri, out var refr);
        ok.Should().BeTrue();
        refr.Should().NotBeNull();
        refr!.Provider.Should().Be(provider);
        refr.Path.Should().Be(path);
        refr.Field.Should().Be(field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("https://example.com")]
    [InlineData("secret://")]
    [InlineData("secret://justprovider")]
    [InlineData("secret://provider/")]
    public void TryParse_RejectsInvalidUris(string uri)
    {
        SecretReference.TryParse(uri, out _).Should().BeFalse();
    }

    [Fact]
    public void ToString_RoundTripsTheUri()
    {
        var r = new SecretReference("vault", "kv/data/foo", "key");
        r.ToString().Should().Be("secret://vault/kv/data/foo#key");
        new SecretReference("aws", "my-secret", null).ToString().Should().Be("secret://aws/my-secret");
    }
}
