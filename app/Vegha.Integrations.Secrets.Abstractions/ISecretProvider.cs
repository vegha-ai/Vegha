namespace Vegha.Integrations.Secrets;

/// <summary>
/// Provides on-demand secret resolution from an external source (HashiCorp Vault,
/// AWS Secrets Manager, Azure Key Vault, etc.). Surfaced to the rest of the app
/// through a registry so URI-style references like <c>secret://vault/acme/prod#api-key</c>
/// can be resolved at request-execution time.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Identifies this provider in <c>secret://&lt;name&gt;/...</c> URIs. Lowercase
    /// (e.g., "vault", "aws", "azure"). Two providers cannot share a name.</summary>
    string Name { get; }

    /// <summary>Resolves the value at <paramref name="path"/> with optional <paramref name="field"/>.
    /// Path syntax is provider-specific — Vault uses <c>kv/data/...</c>, AWS uses the secret ID,
    /// Azure uses the secret name. Returns null when the secret can't be found; throws when
    /// auth fails or the call errors.</summary>
    Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default);
}

/// <summary>Parsed <c>secret://provider/path#field</c> URI.</summary>
public sealed record SecretReference(string Provider, string Path, string? Field)
{
    public override string ToString()
    {
        var field = string.IsNullOrEmpty(Field) ? string.Empty : "#" + Field;
        return $"secret://{Provider}/{Path}{field}";
    }

    /// <summary>Tries to parse a <c>secret://provider/path#field</c> URI.
    /// Returns false (and a null reference) for any other shape.</summary>
    public static bool TryParse(string raw, out SecretReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(raw)) return false;
        if (!raw.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = raw["secret://".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        var provider = rest[..slash];
        var pathAndField = rest[(slash + 1)..];
        string path;
        string? field = null;
        var hash = pathAndField.IndexOf('#');
        if (hash >= 0)
        {
            path = pathAndField[..hash];
            field = pathAndField[(hash + 1)..];
        }
        else
        {
            path = pathAndField;
        }
        if (string.IsNullOrEmpty(path)) return false;
        reference = new SecretReference(provider.ToLowerInvariant(), path, field);
        return true;
    }
}
