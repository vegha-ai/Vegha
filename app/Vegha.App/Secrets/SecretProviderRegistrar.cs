using Vegha.Core.Persistence;
using Vegha.Integrations.Secrets;
using Vegha.Integrations.Secrets.Aws;
using Vegha.Integrations.Secrets.Azure;

namespace Vegha.App.Secrets;

/// <summary>
/// Builds <see cref="ISecretProvider"/> adapters from the persisted, encrypted
/// <see cref="SecretProviderConfig"/> entries and (re)populates the
/// <see cref="SecretRegistry"/>. Run once at startup and again whenever the Secret Manager
/// settings page changes, so <c>secret://&lt;name&gt;/path#field</c> URIs resolve against
/// the user's configured vaults.
///
/// Providers are keyed by the user's config <see cref="SecretProviderConfig.Name"/> — not
/// the adapter type — so several vaults of the same kind can coexist and a URI like
/// <c>secret://prod-azure/api-key</c> targets a specific one.
/// </summary>
public static class SecretProviderRegistrar
{
    /// <summary>Clears the registry and re-registers every configured provider. A config
    /// whose adapter rejects it (missing required fields, unknown type) is skipped so one
    /// bad entry doesn't sink the rest.</summary>
    public static void Reload(SecretRegistry registry, SecretProviderConfigStore? store = null)
    {
        store ??= new SecretProviderConfigStore();
        registry.Clear();
        foreach (var config in store.Load())
        {
            var provider = TryCreate(config);
            if (provider is not null)
                registry.Register(config.Name, provider);
        }
    }

    /// <summary>Maps a config to its provider adapter. Returns null for an unknown type or a
    /// config that fails its adapter's validation.</summary>
    public static ISecretProvider? TryCreate(SecretProviderConfig config)
    {
        try
        {
            return config.Type switch
            {
                "azure" => AzureKeyVaultProvider.FromConfig(config.Settings),
                "aws" => AwsSecretsProvider.FromConfig(config.Settings),
                _ => null,
            };
        }
        catch
        {
            // Malformed config — skip it. The Secret Manager settings page is where the
            // user fixes the entry; a bad one shouldn't break registration of the others.
            return null;
        }
    }
}
