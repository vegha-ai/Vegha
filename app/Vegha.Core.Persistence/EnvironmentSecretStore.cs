using System.Text.Json;

namespace Vegha.Core.Persistence;

/// <summary>
/// Stores the literal values of environment variables flagged as secret. Values are kept
/// out of the committable <c>environments/*.env.json</c> files and instead live encrypted
/// in a per-environment sidecar at <c>&lt;collection&gt;/.secrets/env-&lt;envId&gt;.enc</c>.
///
/// The AES-256-GCM key is per-user and machine-local — <c>%LocalAppData%/Vegha/env-secrets.key</c>
/// — so secrets do not roam with the collection: cloning the repo onto a fresh machine
/// yields empty secret values that must be re-entered (or supplied via the
/// <c>VEGHA_SECRET_&lt;NAME&gt;</c> override). Even a copy of the <c>.secrets/</c> folder
/// is useless without that key.
/// </summary>
public sealed class EnvironmentSecretStore
{
    private const string KeyFileName = "env-secrets.key";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly string _keyPath;
    private readonly object _writeLock = new();

    public EnvironmentSecretStore() : this(DefaultKeyDir()) { }

    /// <summary>Test-friendly ctor: pass an explicit directory for the key file.</summary>
    public EnvironmentSecretStore(string keyDirectory)
    {
        Directory.CreateDirectory(keyDirectory);
        _keyPath = Path.Combine(keyDirectory, KeyFileName);
    }

    private static string DefaultKeyDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha");

    private static string SecretsDir(string collectionRoot) =>
        Path.Combine(collectionRoot, CollectionSecretProviderConfigStore.FolderName);

    private static string FileFor(string collectionRoot, string envId) =>
        Path.Combine(SecretsDir(collectionRoot), "env-" + Sanitize(envId) + ".enc");

    /// <summary>Decrypts the secret values for an environment. Returns an empty map when the
    /// sidecar is missing, corrupt, or undecryptable — never throws, so a collection cloned
    /// without its key file still loads.</summary>
    public IReadOnlyDictionary<string, string> Load(string collectionRoot, string envId)
    {
        var path = FileFor(collectionRoot, envId);
        if (!File.Exists(path)) return EmptyMap;
        try
        {
            var ciphertext = File.ReadAllBytes(path);
            var key = LocalCipher.GetOrCreateKey(_keyPath);
            var json = LocalCipher.Decrypt(ciphertext, key);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, s_json) ?? EmptyMap;
        }
        catch
        {
            return EmptyMap;
        }
    }

    /// <summary>Encrypts and writes the secret values for an environment. An empty (or null)
    /// map deletes the sidecar so no stale ciphertext lingers. Also ensures the
    /// <c>.secrets/</c> folder carries a <c>.gitignore</c> of <c>*</c>.</summary>
    public void Save(string collectionRoot, string envId, IReadOnlyDictionary<string, string>? secretValues)
    {
        lock (_writeLock)
        {
            try
            {
                var path = FileFor(collectionRoot, envId);
                if (secretValues is null || secretValues.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                var dir = SecretsDir(collectionRoot);
                Directory.CreateDirectory(dir);
                EnsureGitignore(dir);
                var json = JsonSerializer.SerializeToUtf8Bytes(secretValues, s_json);
                var key = LocalCipher.GetOrCreateKey(_keyPath);
                File.WriteAllBytes(path, LocalCipher.Encrypt(json, key));
            }
            catch
            {
                /* best-effort — a failed write surfaces as empty secrets on next load */
            }
        }
    }

    /// <summary>Removes an environment's sidecar (called when the environment is deleted).</summary>
    public void Delete(string collectionRoot, string envId)
    {
        lock (_writeLock)
        {
            try
            {
                var path = FileFor(collectionRoot, envId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort */ }
        }
    }

    private static void EnsureGitignore(string secretsDir)
    {
        var gitignore = Path.Combine(secretsDir, ".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore, "*\n");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>();
}
