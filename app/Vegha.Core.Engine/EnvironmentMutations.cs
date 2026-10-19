using DomainEnv = Vegha.Core.Domain.Environment;
using Vegha.Core.Domain;

namespace Vegha.Core.Engine;

/// <summary>
/// Applies the env-var changes a request made (<c>bru.setEnvVar</c> / <c>deleteEnvVar</c>,
/// env-scoped captures) to the active environments in memory. Changes are NOT written to the
/// environment's file — they live for the session until the user explicitly saves the
/// environment (Bruno-style runtime override).
///
/// Updates target the variable wherever it already lives (collection env first, then the
/// workspace/global env); brand-new variables land in the active collection env, or the
/// global env when no collection env is active. Global-targeted changes (global captures)
/// write the global env only.
/// </summary>
public static class EnvironmentMutations
{
    /// <summary>Applies <paramref name="updated"/> and <paramref name="removed"/>; returns
    /// true when either environment changed. A no-op when no environment is active.</summary>
    public static bool Apply(
        DomainEnv? collectionEnv,
        DomainEnv? globalEnv,
        IReadOnlyDictionary<string, string> updated,
        IReadOnlyCollection<string> removed,
        bool targetGlobal = false)
    {
        var newVarTarget = targetGlobal ? globalEnv : collectionEnv ?? globalEnv;
        var changed = false;

        foreach (var (name, value) in updated)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (targetGlobal)
            {
                // Global-scoped captures write the global env only, even when a collection env
                // defines the same name.
                if (TrySetExisting(globalEnv, name, value)) { changed = true; continue; }
            }
            else if (TrySetExisting(collectionEnv, name, value) || TrySetExisting(globalEnv, name, value))
            {
                changed = true;
                continue;
            }
            if (newVarTarget is not null)
            {
                newVarTarget.Variables.Add(new KvPair(name, value));
                changed = true;
            }
        }

        foreach (var name in removed)
        {
            if (string.IsNullOrEmpty(name)) continue;
            changed |= Remove(collectionEnv, name);
            changed |= Remove(globalEnv, name);
        }

        return changed;
    }

    /// <summary>Replaces the value of an existing enabled-or-not variable by name (KvPair is
    /// immutable, so the list entry is swapped, preserving Enabled / Description). Returns false
    /// when the variable isn't present, letting the caller fall through to the next scope.</summary>
    private static bool TrySetExisting(DomainEnv? env, string name, string value)
    {
        if (env is null) return false;
        var vars = env.Variables;
        for (var i = 0; i < vars.Count; i++)
        {
            if (string.Equals(vars[i].Name, name, StringComparison.Ordinal))
            {
                vars[i] = vars[i] with { Value = value };
                return true;
            }
        }
        return false;
    }

    /// <summary>Removes every entry matching <paramref name="name"/> from an env's variables.</summary>
    private static bool Remove(DomainEnv? env, string name)
    {
        if (env is null) return false;
        var vars = env.Variables;
        var removedAny = false;
        for (var i = vars.Count - 1; i >= 0; i--)
        {
            if (string.Equals(vars[i].Name, name, StringComparison.Ordinal))
            {
                vars.RemoveAt(i);
                removedAny = true;
            }
        }
        return removedAny;
    }
}
