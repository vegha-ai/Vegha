using System.Text;

namespace Vegha.Core.Persistence;

/// <summary>Round-trip obfuscation for the proxy password field stored in
/// <see cref="AppSettings"/>. Uses base64 with a versioned prefix so we can swap in real
/// protection (DPAPI on Windows, libsecret on Linux, Keychain on macOS) later without a
/// migration step — the prefix tags identify which scheme produced the string.
///
/// This is intentionally not strong encryption: an attacker with file-system access to
/// the user's <c>%LocalAppData%/Vegha/</c> would also have access to any key we kept
/// next to the settings, so adding ceremony here would be theater. The proxy password is
/// the lowest-stakes secret the app stores — real secret-manager credentials go through
/// <see cref="SecretProviderConfigStore"/> (AES-GCM with a local key) instead.</summary>
public static class ProxyCredentialProtector
{
    private const string Prefix = "v1:b64:";

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>Reverses <see cref="Protect"/>. Tolerant of legacy plaintext values: anything
    /// without the <c>v1:</c> prefix is returned as-is so settings written by older builds
    /// continue to work.</summary>
    public static string Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;
        if (!encrypted.StartsWith("v1:", StringComparison.Ordinal)) return encrypted;
        if (!encrypted.StartsWith(Prefix, StringComparison.Ordinal)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(encrypted[Prefix.Length..]);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
