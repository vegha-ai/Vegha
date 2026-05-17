using Vegha.Integrations.Secrets;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Vegha.Integrations.Secrets.Vault;

/// <summary>
/// HashiCorp Vault adapter. Speaks KV v2 ("kv-v2/data/<path>") and KV v1 against any
/// configured mount point. Auth methods covered: token (env or explicit), AppRole.
/// OIDC + Kubernetes layered later — they need an interactive flow that's not
/// hosted-backend-friendly.
///
/// The secret path may include the mount: <c>kv/data/acme/prod</c>. The field selector
/// is required for KV v2 stores (which return a JSON object); KV v1 returns a single
/// value and ignores the field.
/// </summary>
public sealed class VaultSecretProvider : ISecretProvider
{
    private readonly IVaultClient _client;

    public string Name => "vault";

    private VaultSecretProvider(IVaultClient client) { _client = client; }

    public static VaultSecretProvider FromToken(string vaultAddress, string token)
    {
        var auth = new TokenAuthMethodInfo(token);
        var settings = new VaultClientSettings(vaultAddress, auth);
        return new VaultSecretProvider(new VaultClient(settings));
    }

    public static VaultSecretProvider FromAppRole(string vaultAddress, string roleId, string secretId)
    {
        var auth = new AppRoleAuthMethodInfo(roleId, secretId);
        var settings = new VaultClientSettings(vaultAddress, auth);
        return new VaultSecretProvider(new VaultClient(settings));
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        // Path conventions:
        //   kv-v2 paths look like "secret/data/acme/prod" — VaultSharp wants the unmounted suffix
        //   plus mountPoint. We sniff for "/data/" to pick KV v2; otherwise treat as v1.
        if (path.Contains("/data/", StringComparison.Ordinal))
        {
            var idx = path.IndexOf("/data/", StringComparison.Ordinal);
            var mount = path[..idx];
            var inner = path[(idx + "/data/".Length)..];
            var resp = await _client.V1.Secrets.KeyValue.V2.ReadSecretAsync(inner, mountPoint: mount).ConfigureAwait(false);
            if (resp?.Data?.Data is null) return null;
            if (string.IsNullOrEmpty(field))
                return System.Text.Json.JsonSerializer.Serialize(resp.Data.Data);
            return resp.Data.Data.TryGetValue(field, out var v) ? v?.ToString() : null;
        }
        else
        {
            // KV v1: path itself is the full lookup; field optional inside the dictionary.
            var resp = await _client.V1.Secrets.KeyValue.V1.ReadSecretAsync(path).ConfigureAwait(false);
            if (resp?.Data is null) return null;
            if (string.IsNullOrEmpty(field))
                return System.Text.Json.JsonSerializer.Serialize(resp.Data);
            return resp.Data.TryGetValue(field, out var v) ? v?.ToString() : null;
        }
    }
}
