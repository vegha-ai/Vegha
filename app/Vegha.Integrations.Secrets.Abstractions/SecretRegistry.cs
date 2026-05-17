using System.Collections.Concurrent;

namespace Vegha.Integrations.Secrets;

/// <summary>
/// Global registry of <see cref="ISecretProvider"/> instances keyed by provider name. Resolves
/// <c>secret://provider/path#field</c> URIs and caches results for a TTL so a single request
/// touching the same secret multiple times only hits the backend once.
/// </summary>
public sealed class SecretRegistry
{
    private readonly ConcurrentDictionary<string, ISecretProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedValue> _cache = new(StringComparer.Ordinal);

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Registers a provider under an explicit key — the token used in
    /// <c>secret://&lt;name&gt;/...</c> URIs. Pass the user's provider-config name so
    /// multiple providers of the same type (two Key Vaults, say) can coexist.</summary>
    public void Register(string name, ISecretProvider provider) =>
        _providers[name] = provider;

    /// <summary>Registers a provider under its own <see cref="ISecretProvider.Name"/>.</summary>
    public void Register(ISecretProvider provider) =>
        Register(provider.Name, provider);

    public bool TryGetProvider(string name, out ISecretProvider? provider) =>
        _providers.TryGetValue(name, out provider!);

    public IReadOnlyCollection<string> ProviderNames => _providers.Keys.ToArray();

    /// <summary>Resolves a <c>secret://...</c> URI. Returns null if the URI doesn't parse,
    /// the provider isn't registered, or the secret is missing.</summary>
    public async Task<string?> ResolveAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!SecretReference.TryParse(uri, out var reference) || reference is null) return null;

        var key = reference.ToString();
        if (_cache.TryGetValue(key, out var cached) && cached.IsFresh) return cached.Value;

        if (!_providers.TryGetValue(reference.Provider, out var provider) || provider is null)
            return null;

        var value = await provider.GetSecretAsync(reference.Path, reference.Field, cancellationToken).ConfigureAwait(false);
        if (value is not null)
            _cache[key] = new CachedValue(value, DateTime.UtcNow + CacheTtl);
        return value;
    }

    /// <summary>Pre-resolves a variable bag: any value that is a <c>secret://provider/path#field</c>
    /// URI is replaced with the resolved secret. Non-secret values pass through unchanged, and a
    /// URI that fails to resolve keeps its literal text (so the placeholder is visible rather
    /// than silently blank). Returns a fresh dictionary — the input is not mutated. Call this
    /// once, before the synchronous <c>Interpolator.Resolve</c> pass, so request execution sees
    /// already-resolved values without every call site needing an async secret lookup.</summary>
    public async Task<Dictionary<string, string>> ResolveSecretsAsync(
        IReadOnlyDictionary<string, string> vars, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(vars.Count, StringComparer.Ordinal);
        foreach (var kv in vars)
        {
            if (!string.IsNullOrEmpty(kv.Value)
                && kv.Value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = await ResolveAsync(kv.Value, cancellationToken).ConfigureAwait(false);
                result[kv.Key] = resolved ?? kv.Value;
            }
            else
            {
                result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>Drops every cached secret. Call after the user changes auth on a provider.</summary>
    public void InvalidateCache() => _cache.Clear();

    /// <summary>Removes every registered provider and clears the cache. Used when the host
    /// re-scopes the registry on collection switch — old providers belong to the previous
    /// collection's secret store and must not leak across boundaries.</summary>
    public void Clear()
    {
        _providers.Clear();
        _cache.Clear();
    }

    private sealed record CachedValue(string Value, DateTime ExpiresAt)
    {
        public bool IsFresh => DateTime.UtcNow < ExpiresAt;
    }
}
