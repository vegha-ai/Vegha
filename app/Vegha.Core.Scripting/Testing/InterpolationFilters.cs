using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Scripting.Testing;

/// <summary>
/// Connects <see cref="ValueFilters"/> to <c>{{var | filter}}</c> interpolation. The
/// interpolation layer can't reference this assembly, so the pipeline is registered on
/// <see cref="Interpolator.FilterPipeline"/> when this assembly loads.
/// </summary>
public static class InterpolationFilters
{
    // A library module initializer is deliberate here: the interpolation layer exposes a hook
    // it can't fill itself, and every host that interpolates request fields also loads this
    // assembly (JintHost), so registering on load needs no per-host wiring.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Interpolator.FilterPipeline ??= Apply;

    /// <summary>Applies <paramref name="pipeline"/> (<c>"btoa | upper"</c>) to a variable's text.
    /// JSON array / object text is parsed so <c>{{items | first}}</c> works on captured arrays.
    /// Returns null when a filter is unknown or fails, leaving the placeholder literal.</summary>
    public static string? Apply(string value, string pipeline, Func<string, string?> lookupVar)
    {
        var filters = new List<FilterCall>();
        foreach (var part in ResponseExpression.SplitTopLevel(pipeline, '|'))
        {
            if (!ResponseExpression.TryParseCall(part.Trim(), out var name, out var args, allowBare: true)
                || !ValueFilters.IsKnown(name))
                return null;
            filters.Add(new FilterCall(name, args));
        }
        try
        {
            return ValueFilters.ApplyAll(ToValue(value), filters, lookupVar).ToVariableString();
        }
        catch (FilterException)
        {
            return null;
        }
    }

    private static ResolvedValue ToValue(string text)
    {
        var t = text.TrimStart();
        if (t.Length > 0 && (t[0] == '[' || t[0] == '{'))
        {
            try { return ResolvedValue.Of(JsonNode.Parse(text)); }
            catch (JsonException) { }
        }
        return ResolvedValue.Of(text);
    }
}
