using System.Security.Cryptography;

namespace Vegha.Core.Persistence;

/// <summary>
/// Shared AES-256-GCM helper for the local secret stores. Ciphertext layout is
/// <c>nonce || tag || ciphertext</c>; the key is a 32-byte file created with restricted
/// permissions and scoped to the current user account.
///
/// AES-GCM is used over Microsoft.AspNetCore.DataProtection so this assembly stays
/// dependency-light. On Windows you could swap in <c>ProtectedData</c> (DPAPI) — same
/// shape — but the AES path works identically on macOS + Linux for non-roamed local
/// secrets.
/// </summary>
internal static class LocalCipher
{
    public const int KeyBytes = 32;     // AES-256
    private const int NonceBytes = 12;  // GCM standard
    private const int TagBytes = 16;

    /// <summary>Reads the 32-byte key at <paramref name="keyPath"/>, generating and
    /// persisting a fresh random key (with restricted permissions) when absent.</summary>
    public static byte[] GetOrCreateKey(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == KeyBytes) return existing;
        }
        var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, fresh);
        TryRestrictPermissions(keyPath);
        return fresh;
    }

    /// <summary>Best-effort lockdown: 0600 on POSIX. On Windows the default ACLs under
    /// the user profile already restrict to the current user.</summary>
    public static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* tolerate */ }
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceBytes + TagBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, output, NonceBytes, TagBytes);
        Buffer.BlockCopy(ciphertext, 0, output, NonceBytes + TagBytes, ciphertext.Length);
        return output;
    }

    public static byte[] Decrypt(byte[] input, byte[] key)
    {
        if (input.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Ciphertext is truncated.");
        var nonce = new byte[NonceBytes];
        var tag = new byte[TagBytes];
        var ciphertext = new byte[input.Length - NonceBytes - TagBytes];
        Buffer.BlockCopy(input, 0, nonce, 0, NonceBytes);
        Buffer.BlockCopy(input, NonceBytes, tag, 0, TagBytes);
        Buffer.BlockCopy(input, NonceBytes + TagBytes, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
