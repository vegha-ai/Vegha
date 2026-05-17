using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using FluentAssertions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.FileFormat;

/// <summary>Round-trip coverage for the secret-stripping behaviour wired into
/// <see cref="CollectionStore"/> — literal secret values must not be written into the
/// committable <c>*.env.json</c> files, yet must survive a save/load cycle.</summary>
public class CollectionStoreSecretsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vegha-store-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Collection CollectionWithSecretEnv() => new()
    {
        Name = "Demo",
        Requests = new List<RequestItem>(),
        Folders = new List<Folder>(),
        Environments = new List<DomainEnv>
        {
            new()
            {
                Id = "env-prod",
                Name = "Prod",
                Variables = new List<KvPair>
                {
                    new("host", "api.example.com"),
                    new("api_key", "literal-secret-value-123"),
                },
                SecretVariables = new List<string> { "api_key" },
            },
        },
    };

    [Fact]
    public void Save_DoesNotWriteLiteralSecretIntoEnvFile()
    {
        CollectionStore.Save(_root, CollectionWithSecretEnv());

        var envFile = Path.Combine(_root, "environments", "Prod.env.json");
        File.Exists(envFile).Should().BeTrue();
        var json = File.ReadAllText(envFile);

        json.Should().NotContain("literal-secret-value-123");
        json.Should().Contain("api_key");          // the name is still listed
        json.Should().Contain("api.example.com");  // non-secret value stays inline
    }

    [Fact]
    public void SaveThenLoad_RestoresSecretValue()
    {
        CollectionStore.Save(_root, CollectionWithSecretEnv());
        var loaded = CollectionStore.Load(_root);

        var env = loaded.Environments.Single();
        env.Variables.Single(v => v.Name == "api_key").Value.Should().Be("literal-secret-value-123");
        env.SecretVariables.Should().Contain("api_key");
    }

    [Fact]
    public void Save_WritesEncryptedSidecarUnderSecretsFolder()
    {
        CollectionStore.Save(_root, CollectionWithSecretEnv());

        var sidecar = Path.Combine(_root, ".secrets", "env-env-prod.enc");
        File.Exists(sidecar).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(sidecar))
            .Should().NotContain("literal-secret-value-123");
    }
}
