using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>One configured secret provider — name + type + arbitrary key/value config.
/// Sensitive bits (Vault tokens, AWS access keys, etc.) live inside <see cref="Settings"/>;
/// the whole record is encrypted on disk so the user's secrets-manager creds aren't sitting
/// in plaintext under <c>%LocalAppData%</c>.</summary>
public sealed record SecretProviderConfig(
    string Name,
    string Type,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Persists the user's configured secret providers to
/// <c>%LocalAppData%/Vegha/secret-providers.json</c>. The body is AES-GCM encrypted
/// against a per-user key kept alongside the file at <c>secret-providers.key</c>; the key
/// file is created with restricted permissions and exists only for this user account.
///
/// We chose AES-GCM over Microsoft.AspNetCore.DataProtection so this assembly can stay
/// dependency-light (Core.Persistence has no NuGet refs). On Windows you could swap in
/// <c>System.Security.Cryptography.ProtectedData</c> — same shape — but the AES path
/// works identically on Mac + Linux for non-roamed local secrets.
/// </summary>
public sealed class SecretProviderConfigStore
{
    private const string FileName = "secret-providers.json";
    private const string KeyFileName = "secret-providers.key";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly object _writeLock = new();

    public SecretProviderConfigStore() : this(DefaultDir()) { }

    /// <summary>Test-friendly ctor: pass an explicit directory.</summary>
    public SecretProviderConfigStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, FileName);
        _keyPath = Path.Combine(directory, KeyFileName);
    }

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha");

    public IReadOnlyList<SecretProviderConfig> Load()
    {
        if (!File.Exists(_filePath)) return Array.Empty<SecretProviderConfig>();
        try
        {
            var ciphertext = File.ReadAllBytes(_filePath);
            var key = LocalCipher.GetOrCreateKey(_keyPath);
            var json = LocalCipher.Decrypt(ciphertext, key);
            return JsonSerializer.Deserialize<List<SecretProviderConfig>>(json, s_json)
                ?? new List<SecretProviderConfig>();
        }
        catch
        {
            // Corrupt or unreadable — return empty so the user can re-add. We don't auto-delete
            // the bad file; surfacing the issue belongs in the panel.
            return Array.Empty<SecretProviderConfig>();
        }
    }

    public void Save(IReadOnlyList<SecretProviderConfig> configs)
    {
        lock (_writeLock)
        {
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(configs, s_json);
                var key = LocalCipher.GetOrCreateKey(_keyPath);
                var ciphertext = LocalCipher.Encrypt(json, key);
                File.WriteAllBytes(_filePath, ciphertext);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

}
