using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class EnvironmentSecretStoreTests : IDisposable
{
    private readonly string _root;       // pretend collection root
    private readonly string _keyDir;     // pretend %LocalAppData%/Vegha

    public EnvironmentSecretStoreTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vegha-env-secret-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "collection");
        _keyDir = Path.Combine(baseDir, "appdata");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_keyDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best-effort */ }
    }

    private EnvironmentSecretStore NewStore() => new(_keyDir);

    [Fact]
    public void Save_ThenLoad_RoundTripsValues()
    {
        var store = NewStore();
        var values = new Dictionary<string, string> { ["api_key"] = "hunter2-xyz", ["db_pass"] = "p@ss-word" };

        store.Save(_root, "env-1", values);
        var loaded = store.Load(_root, "env-1");

        loaded.Should().HaveCount(2);
        loaded["api_key"].Should().Be("hunter2-xyz");
        loaded["db_pass"].Should().Be("p@ss-word");
    }

    [Fact]
    public void Load_MissingSidecar_ReturnsEmpty()
    {
        NewStore().Load(_root, "never-saved").Should().BeEmpty();
    }

    [Fact]
    public void Save_WritesGitignoreCoveringTheSecretsFolder()
    {
        NewStore().Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "value-1234" });

        var gitignore = Path.Combine(_root, ".secrets", ".gitignore");
        File.Exists(gitignore).Should().BeTrue();
        File.ReadAllText(gitignore).Trim().Should().Be("*");
    }

    [Fact]
    public void Save_FileOnDisk_IsNotPlaintext()
    {
        NewStore().Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "SUPER-SECRET-VALUE" });

        var enc = Path.Combine(_root, ".secrets", "env-env-1.enc");
        File.Exists(enc).Should().BeTrue();
        var asText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(enc));
        asText.Should().NotContain("SUPER-SECRET-VALUE");
    }

    [Fact]
    public void Save_EmptyMap_DeletesSidecar()
    {
        var store = NewStore();
        store.Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "value-1234" });
        var enc = Path.Combine(_root, ".secrets", "env-env-1.enc");
        File.Exists(enc).Should().BeTrue();

        store.Save(_root, "env-1", new Dictionary<string, string>());
        File.Exists(enc).Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesSidecar()
    {
        var store = NewStore();
        store.Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "value-1234" });
        store.Delete(_root, "env-1");

        File.Exists(Path.Combine(_root, ".secrets", "env-env-1.enc")).Should().BeFalse();
        store.Load(_root, "env-1").Should().BeEmpty();
    }

    [Fact]
    public void Load_TamperedCiphertext_ReturnsEmpty_DoesNotThrow()
    {
        var store = NewStore();
        store.Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "value-1234" });

        var enc = Path.Combine(_root, ".secrets", "env-env-1.enc");
        var bytes = File.ReadAllBytes(enc);
        bytes[^3] ^= 0xFF;
        File.WriteAllBytes(enc, bytes);

        store.Load(_root, "env-1").Should().BeEmpty();
    }

    [Fact]
    public void Load_WithoutKeyFile_ReturnsEmpty_DoesNotThrow()
    {
        // A collection cloned onto a fresh machine carries the .secrets ciphertext but
        // not the per-user key. Decryption fails gracefully to empty.
        new EnvironmentSecretStore(_keyDir)
            .Save(_root, "env-1", new Dictionary<string, string> { ["k"] = "value-1234" });

        var freshKeyDir = Path.Combine(Path.GetDirectoryName(_root)!, "appdata-fresh");
        var loaded = new EnvironmentSecretStore(freshKeyDir).Load(_root, "env-1");

        loaded.Should().BeEmpty();
    }
}
