using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.FileFormat;

/// <summary>Pins the security fix: workspace-level (global) env files are stripped of literal
/// secrets on write, and <see cref="WorkspaceModelLoader.Load"/> merges them back from the
/// encrypted sidecar on read (mirroring <c>CollectionStore</c>). Before the fix the loader
/// never merged, so a "secret" var came back blank after a stripped write.</summary>
public class WorkspaceModelLoaderSecretTests : IDisposable
{
    private readonly string _wsRoot;

    public WorkspaceModelLoaderSecretTests()
    {
        _wsRoot = Path.Combine(Path.GetTempPath(), "vegha-wsmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wsRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Load_MergesSecretValues_FromSidecar()
    {
        var store = new EnvironmentSecretStore();
        var env = new DomainEnv
        {
            Id = "gw1",
            Name = "Prod",
            Variables = new List<KvPair> { new("api_key", "top-secret-123"), new("host", "prod.example.com") },
            SecretVariables = new List<string> { "api_key" },
        };

        // Strip literal secrets to the sidecar (keyed by the workspace root), then write the
        // masked env file into <workspace>/environments/ exactly as the global env editor does.
        var stripped = EnvironmentSecretSplitter.StripForPersistence(env, _wsRoot, store);
        var envDir = Path.Combine(_wsRoot, WorkspaceModelLoader.EnvironmentsFolder);
        Directory.CreateDirectory(envDir);
        File.WriteAllText(
            Path.Combine(envDir, "Prod" + CollectionJson.EnvironmentSuffix),
            CollectionJson.SerializeEnvironment(EnvironmentFile.FromDomain(stripped)));

        // On disk the secret is blank; the loader must restore it from the sidecar.
        var model = WorkspaceModelLoader.Load(_wsRoot);
        var loaded = model.Environments.Single(e => e.Name == "Prod");
        loaded.Variables.Single(v => v.Name == "api_key").Value.Should().Be("top-secret-123");
        loaded.Variables.Single(v => v.Name == "host").Value.Should().Be("prod.example.com");
    }
}
