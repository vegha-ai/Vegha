using System.Text;

namespace Vegha.Core.Interpolation;

/// <summary>
/// Resolves <c>{{name}}</c> placeholders against a variable bag. Supports nested references
/// (a variable's value can itself contain <c>{{other}}</c>) with cycle detection.
///
/// Mirrors <c>bruno-common/src/interpolate/index.ts</c>. Postman-style dynamic variables
/// (<c>{{$randomUUID}}</c>, <c>{{$timestamp}}</c>, …) are resolved by
/// <see cref="DynamicVariableProvider"/> when the placeholder starts with <c>$</c>.
/// </summary>
public static class Interpolator
{
    private const int MaxDepth = 32;

    /// <summary>Resolve placeholders against the supplied flat dictionary. Postman dynamic
    /// variables (<c>{{$randomUUID}}</c> etc.) are resolved automatically.</summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string> vars) =>
        Resolve(template, name =>
        {
            if (name.Length > 0 && name[0] == '$' &&
                DynamicVariableProvider.TryResolve(name, out var dyn)) return dyn;
            return vars.TryGetValue(name, out var v) ? v : null;
        });

    /// <summary>Resolve placeholders against a variable dictionary plus an optional secret
    /// resolver for <c>secret://provider/path#field</c> URIs. The secret resolver is checked
    /// first when the placeholder text starts with <c>secret://</c>; otherwise dynamic
    /// variables and the variable dictionary are consulted.</summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string> vars, Func<string, string?>? secretResolver) =>
        Resolve(template, name =>
        {
            if (secretResolver is not null && name.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
                return secretResolver(name);
            if (name.Length > 0 && name[0] == '$' &&
                DynamicVariableProvider.TryResolve(name, out var dyn)) return dyn;
            return vars.TryGetValue(name, out var v) ? v : null;
        });

    /// <summary>Resolve placeholders using a custom lookup. Return <c>null</c> from the resolver to leave the placeholder literal.</summary>
    public static string Resolve(string template, Func<string, string?> resolver)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;
        return ResolveInternal(template, resolver, new HashSet<string>(), 0);
    }

    private static string ResolveInternal(string template, Func<string, string?> resolver,
        HashSet<string> activeChain, int depth)
    {
        if (depth > MaxDepth) return template;

        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            if (i + 1 < template.Length && template[i] == '{' && template[i + 1] == '{')
            {
                var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    // No closing braces — emit the rest verbatim.
                    sb.Append(template, i, template.Length - i);
                    break;
                }

                var name = template.Substring(i + 2, close - (i + 2)).Trim();

                if (string.IsNullOrEmpty(name))
                {
                    sb.Append("{{}}");
                }
                else if (activeChain.Contains(name))
                {
                    // Cycle — leave literal to avoid infinite expansion.
                    sb.Append("{{").Append(name).Append("}}");
                }
                else
                {
                    var value = resolver(name);
                    if (value is null)
                    {
                        // Unknown — leave literal so the user sees the placeholder unchanged.
                        sb.Append("{{").Append(name).Append("}}");
                    }
                    else
                    {
                        activeChain.Add(name);
                        sb.Append(ResolveInternal(value, resolver, activeChain, depth + 1));
                        activeChain.Remove(name);
                    }
                }

                i = close + 2;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
