using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vegha.Integrations.Git.Credentials;

/// <summary>Stores git secrets (PATs, device-flow tokens) that the user chose to keep inside
/// Vegha rather than in Git Credential Manager.</summary>
public interface IGitSecretVault
{
    string? Get(string key);
    void Set(string key, string secret);
    void Delete(string key);
}

/// <summary>File-backed vault. Each value is protected with DPAPI (CurrentUser scope) on
/// Windows; on macOS / Linux with AES-256-GCM using a per-user key file (0600) next to the
/// vault — the same scheme as Vegha's other local secret stores. In the Mac App Store build both
/// files live in the app's sandbox container, unreadable to other apps.</summary>
public sealed class GitSecretVault : IGitSecretVault
{
    private readonly string _path;
    private readonly string _keyPath;
    private readonly object _gate = new();

    public GitSecretVault(string? folder = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha");
        _path = Path.Combine(folder, "git-secrets.json");
        _keyPath = Path.Combine(folder, "git-secrets.key");
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var map = Load();
            if (!map.TryGetValue(key, out var blob)) return null;
            try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(blob))); }
            catch { return null; }
        }
    }

    public void Set(string key, string secret)
    {
        lock (_gate)
        {
            var map = Load();
            map[key] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(secret)));
            Save(map);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(key)) Save(map);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map));
        RestrictPermissions(tmp);
        File.Move(tmp, _path, overwrite: true);
    }

    private byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return Dpapi.Protect(data);
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }

    private byte[] Unprotect(byte[] blob)
    {
        if (OperatingSystem.IsWindows()) return Dpapi.Unprotect(blob);
        var key = GetOrCreateKey();
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == 32) return existing;
        }
        var fresh = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllBytes(_keyPath, fresh);
        RestrictPermissions(_keyPath);
        return fresh;
    }

    private static void RestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Minimal DPAPI interop (crypt32) — avoids a package dependency for two calls.</summary>
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var output = default(DataBlob);
                var ok = protect
                    ? CryptProtectData(ref input, "Vegha git secret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { handle.Free(); }
        }
    }
}
