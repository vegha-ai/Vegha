using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.Azure;

/// <summary>
/// Azure Key Vault adapter. Data-plane access requires an Entra ID (Azure AD) OAuth 2.0
/// access token — the vault URL alone is never sufficient. Two credential modes are
/// supported, selected by <see cref="BuildCredential"/>:
/// <list type="bullet">
///   <item><description><b>Service principal</b> — when <c>tenantId</c>, <c>clientId</c> and
///   <c>clientSecret</c> are configured, a <see cref="ClientSecretCredential"/> runs the
///   OAuth2 client-credentials flow against Entra ID. The app registration must hold a
///   Key Vault access policy / RBAC role (e.g. "Key Vault Secrets User") on the vault.</description></item>
///   <item><description><b>Ambient</b> — otherwise <see cref="DefaultAzureCredential"/>
///   chains Azure CLI (<c>az login</c>) / managed identity / environment credentials.</description></item>
/// </list>
/// Path is the Key Vault secret name; the <c>field</c> selector is a no-op for Azure
/// (secrets are scalar strings).
/// </summary>
public sealed class AzureKeyVaultProvider : ISecretProvider
{
    private readonly SecretClient _client;

    public string Name => "azure";

    public AzureKeyVaultProvider(string vaultUri, TokenCredential credential)
        : this(new SecretClient(new Uri(vaultUri), credential)) { }

    public AzureKeyVaultProvider(SecretClient client) { _client = client; }

    /// <summary>Builds a provider from a <c>SecretProviderConfig.Settings</c> dictionary.
    /// Recognised keys: <c>vaultUri</c> (required), <c>tenantId</c>, <c>clientId</c>,
    /// <c>clientSecret</c>.</summary>
    public static AzureKeyVaultProvider FromConfig(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("vaultUri", out var vaultUri) || string.IsNullOrWhiteSpace(vaultUri))
            throw new ArgumentException("Azure Key Vault configuration requires a 'vaultUri'.");
        return new AzureKeyVaultProvider(new SecretClient(new Uri(vaultUri), BuildCredential(settings)));
    }

    /// <summary>Picks the token credential: a service-principal
    /// <see cref="ClientSecretCredential"/> when tenant + client + secret are all present,
    /// otherwise <see cref="DefaultAzureCredential"/>.</summary>
    public static TokenCredential BuildCredential(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue("tenantId", out var tenantId);
        settings.TryGetValue("clientId", out var clientId);
        settings.TryGetValue("clientSecret", out var clientSecret);

        if (!string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(clientSecret))
            return new ClientSecretCredential(tenantId.Trim(), clientId.Trim(), clientSecret);

        return new DefaultAzureCredential();
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await _client.GetSecretAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return resp?.Value?.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
