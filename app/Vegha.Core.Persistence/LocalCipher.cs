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
    /// persisting a fresh random key (with restricted permissions) when absent.
    ///
    /// Several Vegha processes (desktop, CLI, VS Code sidecar) can race to create the key on
    /// a fresh machine. The fresh key is written to a temp file and moved into place without
    /// overwrite, so exactly one process wins and the others adopt the winner's key — a
    /// plain overwrite would orphan everything already encrypted with the loser's key.</summary>
    public static byte[] GetOrCreateKey(string keyPath)
    {
        if (TryReadKey(keyPath) is { } existing) return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
        var temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, fresh);
            TryRestrictPermissions(temp);
            if (!File.Exists(keyPath))
            {
                try
                {
                    File.Move(temp, keyPath, overwrite: false);
                    return fresh;
                }
                catch (IOException) when (File.Exists(keyPath)) { /* another process won */ }
            }

            // Lost the race (or found a file that wasn't a valid key a moment ago): adopt
            // what's on disk once it reads as a complete key.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (TryReadKey(keyPath) is { } winner) return winner;
                Thread.Sleep(25);
            }

            // Still not a valid key — a corrupt/truncated file. Replace it, as before.
            File.Move(temp, keyPath, overwrite: true);
            return fresh;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
        }
    }

    private static byte[]? TryReadKey(string keyPath)
    {
        try
        {
            if (!File.Exists(keyPath)) return null;
            var bytes = File.ReadAllBytes(keyPath);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch (IOException) { return null; }
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
