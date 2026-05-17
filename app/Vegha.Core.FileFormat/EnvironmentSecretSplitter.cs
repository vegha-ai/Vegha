using Vegha.Core.Persistence;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.FileFormat;

/// <summary>
/// Bridges the in-memory <see cref="DomainEnv"/> (which carries full secret values) and the
/// on-disk representation. A secret-flagged variable is stored in exactly one of two
/// <b>mutually exclusive</b> modes, decided solely by its value:
/// <list type="bullet">
///   <item><description><b>Cloud binding</b> — the value is a <c>secret://provider/path#field</c>
///   URI. It is a pointer, not a secret: it stays inline, in plain text, in the committable
///   <c>.env.json</c> and is never written to the encrypted sidecar.</description></item>
///   <item><description><b>Local secret</b> — the value is a literal. It is moved into the
///   encrypted sidecar (see <see cref="EnvironmentSecretStore"/>); the inline entry keeps its
///   name/enabled with an empty value.</description></item>
/// </list>
/// Because the modes are mutually exclusive, binding a variable to a secret manager removes
/// any local-secret value it previously had — on save (the sidecar is rebuilt) and on load
/// (a stale entry is healed away), so a local value can never shadow a cloud binding.
/// </summary>
public static class EnvironmentSecretSplitter
{
    private const string EnvVarPrefix = "VEGHA_SECRET_";

    /// <summary>Returns a copy of <paramref name="env"/> with literal secret values blanked,
    /// having written those values to the encrypted sidecar. Variables bound to a secret
    /// manager (<c>secret://…</c>) are kept inline and excluded from the sidecar. Call before
    /// serializing the environment to <c>.env.json</c>.</summary>
    public static DomainEnv StripForPersistence(DomainEnv env, string collectionRoot, EnvironmentSecretStore store)
    {
        // The sidecar is keyed by env Id; mint one when absent so it matches the Id the
        // env file is serialized with (EnvironmentFile.FromDomain mints independently —
        // stamping it here keeps both sides in lockstep).
        var envId = string.IsNullOrWhiteSpace(env.Id) ? Guid.NewGuid().ToString("N") : env.Id;

        var secretNames = new HashSet<string>(env.SecretVariables, StringComparer.Ordinal);
        var sidecar = new Dictionary<string, string>(StringComparer.Ordinal);
        var stripped = new List<Domain.KvPair>(env.Variables.Count);

        foreach (var v in env.Variables)
        {
            // Only a literal value of a secret-flagged variable goes to the encrypted sidecar.
            // A cloud binding (secret://…) is kept inline — the rebuilt sidecar omits it, so
            // binding a previously-local secret to a manager drops its encrypted value here.
            if (secretNames.Contains(v.Name) && !IsProviderReference(v.Value) && !string.IsNullOrEmpty(v.Value))
            {
                sidecar[v.Name] = v.Value;
                stripped.Add(v with { Value = string.Empty });
            }
            else
            {
                stripped.Add(v);
            }
        }

        store.Save(collectionRoot, envId, sidecar);
        return env with { Id = envId, Variables = stripped };
    }

    /// <summary>Returns a copy of <paramref name="env"/> with local secret values restored
    /// from the <c>VEGHA_SECRET_*</c> override (preferred) or the encrypted sidecar.
    /// A variable bound to a secret manager keeps its inline <c>secret://…</c> URI verbatim;
    /// if the sidecar still holds a stale local value for such a variable it is purged so it
    /// can never shadow the binding. Call after loading the environment from <c>.env.json</c>.</summary>
    public static DomainEnv MergeFromStore(DomainEnv env, string collectionRoot, EnvironmentSecretStore store)
    {
        var secretNames = new HashSet<string>(env.SecretVariables, StringComparer.Ordinal);
        if (secretNames.Count == 0) return env;

        var sidecar = store.Load(collectionRoot, env.Id);
        var merged = new List<Domain.KvPair>(env.Variables.Count);
        var staleCloudBindings = new List<string>();

        foreach (var v in env.Variables)
        {
            if (!secretNames.Contains(v.Name))
            {
                merged.Add(v);
                continue;
            }

            // A cloud binding is authoritative and mutually exclusive with local storage:
            // keep the secret:// URI inline. A sidecar entry for the same variable is stale
            // (it pre-dates the binding) — never overlay it, and schedule it for removal.
            if (IsProviderReference(v.Value))
            {
                if (sidecar.ContainsKey(v.Name)) staleCloudBindings.Add(v.Name);
                merged.Add(v);
                continue;
            }

            // Otherwise it is a local secret: a blanked entry is filled from the CI override
            // or the encrypted sidecar. A non-empty literal is left as-is.
            if (string.IsNullOrEmpty(v.Value))
            {
                var resolved = EnvVarOverride(v.Name)
                    ?? (sidecar.TryGetValue(v.Name, out var s) ? s : null);
                merged.Add(resolved is null ? v : v with { Value = resolved });
            }
            else
            {
                merged.Add(v);
            }
        }

        // Heal: a variable that is now a cloud binding must not retain an encrypted local
        // value. Rewrite the sidecar without those entries (deletes the file if it empties).
        if (staleCloudBindings.Count > 0)
        {
            var cleaned = sidecar
                .Where(kv => !staleCloudBindings.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            store.Save(collectionRoot, env.Id, cleaned);
        }

        return env with { Variables = merged };
    }

    /// <summary>True when the value is a <c>secret://…</c> secret-manager reference.
    /// Leading whitespace is tolerated so a hand-typed value still classifies correctly.</summary>
    private static bool IsProviderReference(string? value) =>
        value is not null && value.TrimStart().StartsWith("secret://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <c>VEGHA_SECRET_&lt;NAME&gt;</c> where NAME is the variable name
    /// uppercased with non-alphanumeric characters replaced by underscores.</summary>
    private static string? EnvVarOverride(string variableName)
    {
        var key = EnvVarPrefix + new string(variableName
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
