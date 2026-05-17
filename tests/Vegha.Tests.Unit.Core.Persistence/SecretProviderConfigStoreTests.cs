using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class SecretProviderConfigStoreTests : IDisposable
{
    private readonly string _dir;

    public SecretProviderConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vegha-secret-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsConfigs()
    {
        var store = new SecretProviderConfigStore(_dir);
        var configs = new List<SecretProviderConfig>
        {
            new("prod-vault", "vault",
                new Dictionary<string, string> { ["url"] = "https://vault.test", ["token"] = "hvs.abc" }),
            new("aws-prod", "aws-secrets-manager",
                new Dictionary<string, string> { ["region"] = "us-east-1", ["accessKey"] = "AKIA..." }),
        };

        store.Save(configs);
        var loaded = store.Load();

        loaded.Should().HaveCount(2);
        loaded[0].Name.Should().Be("prod-vault");
        loaded[0].Settings["token"].Should().Be("hvs.abc");
        loaded[1].Settings["region"].Should().Be("us-east-1");
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var store = new SecretProviderConfigStore(_dir);
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_FileOnDisk_IsNotPlaintext()
    {
        var store = new SecretProviderConfigStore(_dir);
        store.Save(new List<SecretProviderConfig>
        {
            new("x", "vault", new Dictionary<string, string> { ["token"] = "SECRET-VALUE-XYZ" })
        });

        var path = Path.Combine(_dir, "secret-providers.json");
        File.Exists(path).Should().BeTrue();
        var bytes = File.ReadAllBytes(path);
        // Should not contain the plaintext token.
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        asText.Should().NotContain("SECRET-VALUE-XYZ");
    }

    [Fact]
    public void Load_TamperedCiphertext_ReturnsEmpty_DoesNotThrow()
    {
        var store = new SecretProviderConfigStore(_dir);
        store.Save(new List<SecretProviderConfig>
        {
            new("x", "vault", new Dictionary<string, string> { ["token"] = "abc" })
        });

        // Corrupt the ciphertext by flipping a byte deep in the body.
        var path = Path.Combine(_dir, "secret-providers.json");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length - 5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        store.Load().Should().BeEmpty();  // graceful failure, not exception
    }

    [Fact]
    public void Save_ReusesExistingKey_AcrossInstances()
    {
        var store1 = new SecretProviderConfigStore(_dir);
        store1.Save(new List<SecretProviderConfig>
        {
            new("alpha", "vault", new Dictionary<string, string> { ["url"] = "u" })
        });

        var store2 = new SecretProviderConfigStore(_dir);
        var loaded = store2.Load();
        loaded.Should().ContainSingle().Which.Name.Should().Be("alpha");
    }
}
