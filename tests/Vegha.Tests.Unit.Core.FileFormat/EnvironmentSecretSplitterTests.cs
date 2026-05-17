using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.FileFormat;

public class EnvironmentSecretSplitterTests : IDisposable
{
    private readonly string _root;
    private readonly string _keyDir;
    private readonly EnvironmentSecretStore _store;

    public EnvironmentSecretSplitterTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vegha-splitter-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "collection");
        _keyDir = Path.Combine(baseDir, "appdata");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_keyDir);
        _store = new EnvironmentSecretStore(_keyDir);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable("VEGHA_SECRET_API_KEY", null);
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best-effort */ }
    }

    private static DomainEnv EnvWith(IEnumerable<KvPair> vars, params string[] secrets) => new()
    {
        Id = "e1",
        Name = "Local",
        Variables = vars.ToList(),
        SecretVariables = secrets.ToList(),
    };

    [Fact]
    public void StripForPersistence_BlanksLiteralSecret_AndMergeRestoresIt()
    {
        var env = EnvWith(new[] { new KvPair("api_key", "literal-secret-xyz"), new KvPair("host", "example.com") },
            "api_key");

        var stripped = EnvironmentSecretSplitter.StripForPersistence(env, _root, _store);

        stripped.Variables.Single(v => v.Name == "api_key").Value.Should().BeEmpty();
        stripped.Variables.Single(v => v.Name == "host").Value.Should().Be("example.com");

        var merged = EnvironmentSecretSplitter.MergeFromStore(stripped, _root, _store);
        merged.Variables.Single(v => v.Name == "api_key").Value.Should().Be("literal-secret-xyz");
    }

    [Fact]
    public void StripForPersistence_KeepsProviderReferenceInline()
    {
        var env = EnvWith(new[] { new KvPair("api_key", "secret://vault/acme/prod#key") }, "api_key");

        var stripped = EnvironmentSecretSplitter.StripForPersistence(env, _root, _store);

        // A secret:// pointer is not a literal secret — it stays inline, nothing goes to the sidecar.
        stripped.Variables.Single().Value.Should().Be("secret://vault/acme/prod#key");
        _store.Load(_root, "e1").Should().BeEmpty();
    }

    [Fact]
    public void StripForPersistence_LeavesNonSecretVariableUntouched()
    {
        var env = EnvWith(new[] { new KvPair("host", "example.com") });

        var stripped = EnvironmentSecretSplitter.StripForPersistence(env, _root, _store);

        stripped.Variables.Single().Value.Should().Be("example.com");
    }

    [Fact]
    public void MergeFromStore_EnvironmentVariableOverride_BeatsSidecar()
    {
        var env = EnvWith(new[] { new KvPair("api_key", "from-sidecar-value") }, "api_key");
        EnvironmentSecretSplitter.StripForPersistence(env, _root, _store);  // writes sidecar

        System.Environment.SetEnvironmentVariable("VEGHA_SECRET_API_KEY", "from-ci-override");

        // Reload the blanked env (as it would come off disk) and merge.
        var onDisk = EnvWith(new[] { new KvPair("api_key", string.Empty) }, "api_key");
        var merged = EnvironmentSecretSplitter.MergeFromStore(onDisk, _root, _store);

        merged.Variables.Single().Value.Should().Be("from-ci-override");
    }

    [Fact]
    public void MergeFromStore_NoSecrets_ReturnsEnvUnchanged()
    {
        var env = EnvWith(new[] { new KvPair("host", "example.com") });
        var merged = EnvironmentSecretSplitter.MergeFromStore(env, _root, _store);
        merged.Should().BeSameAs(env);
    }

    [Fact]
    public void MergeFromStore_CloudBinding_KeepsUri_AndDoesNotUseStaleSidecar()
    {
        // The variable started as a local secret (value in the sidecar) and was later bound
        // to a secret manager. The env file now holds the secret:// URI; the merge must keep
        // the URI and must NOT overlay the stale local value.
        var asLocalSecret = EnvWith(new[] { new KvPair("api_key", "old-local-value") }, "api_key");
        EnvironmentSecretSplitter.StripForPersistence(asLocalSecret, _root, _store);  // writes sidecar
        _store.Load(_root, "e1").Should().ContainKey("api_key");                     // stale entry present

        var boundToCloud = EnvWith(new[] { new KvPair("api_key", "secret://prod/api-key") }, "api_key");
        var merged = EnvironmentSecretSplitter.MergeFromStore(boundToCloud, _root, _store);

        merged.Variables.Single().Value.Should().Be("secret://prod/api-key");
    }

    [Fact]
    public void MergeFromStore_CloudBinding_PurgesStaleSidecarEntry()
    {
        // Mutual exclusivity: once a variable is a cloud binding, its value must not remain
        // in the encrypted local sidecar.
        var asLocalSecret = EnvWith(new[] { new KvPair("api_key", "old-local-value") }, "api_key");
        EnvironmentSecretSplitter.StripForPersistence(asLocalSecret, _root, _store);

        var boundToCloud = EnvWith(new[] { new KvPair("api_key", "secret://prod/api-key") }, "api_key");
        EnvironmentSecretSplitter.MergeFromStore(boundToCloud, _root, _store);

        _store.Load(_root, "e1").Should().NotContainKey("api_key");
    }

    [Fact]
    public void StripForPersistence_BindingPreviouslyLocalSecret_RemovesItFromSidecar()
    {
        // Save it once as a local secret, then save again bound to a secret manager — the
        // rebuilt sidecar must no longer carry the literal value.
        var asLocalSecret = EnvWith(new[] { new KvPair("api_key", "old-local-value") }, "api_key");
        EnvironmentSecretSplitter.StripForPersistence(asLocalSecret, _root, _store);

        var boundToCloud = EnvWith(new[] { new KvPair("api_key", "secret://prod/api-key") }, "api_key");
        var stripped = EnvironmentSecretSplitter.StripForPersistence(boundToCloud, _root, _store);

        stripped.Variables.Single().Value.Should().Be("secret://prod/api-key");
        _store.Load(_root, "e1").Should().BeEmpty();
    }
}
