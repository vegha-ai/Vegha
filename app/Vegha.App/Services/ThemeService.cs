using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Vegha.Core.Persistence;

namespace Vegha.App.Services;

/// <summary>Drives the application's theme. Two responsibilities:
/// <list type="bullet">
///   <item>Set <see cref="Application.RequestedThemeVariant"/> so Fluent base controls and
///         OS chrome follow the mode (light/dark/system).</item>
///   <item>Swap the per-variant token dictionary inside <c>Application.Resources.MergedDictionaries</c>
///         so app-specific brush keys (Bg*Brush, Text*Brush, etc.) pick up the chosen named variant.</item>
/// </list>
/// The variant dictionary lives at <see cref="VariantSlotIndex"/> inside the merged dictionaries
/// list — that slot is reserved at design time in App.axaml and ONLY this service mutates it.</summary>
public sealed class ThemeService
{
    /// <summary>Index of the variant slot inside <see cref="Application.Resources"/>.MergedDictionaries.
    /// 0 = Shared (theme-agnostic tokens), 1 = the variant being swapped here.</summary>
    private const int VariantSlotIndex = 1;

    private readonly AppSettingsStore _store;

    public ThemeService(AppSettingsStore store)
    {
        _store = store;
    }

    /// <summary>Reads the current settings and applies both mode and variant. Call once at
    /// app startup and again whenever the user saves settings.</summary>
    public void ApplyFromSettings()
    {
        var s = _store.Load();
        ApplyMode(s.ThemeMode);
        ApplyVariantForMode(s.ThemeMode, s.ThemeVariantLight, s.ThemeVariantDark);
    }

    public void ApplyMode(string mode)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = mode.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark"  => ThemeVariant.Dark,
            _       => ThemeVariant.Default,
        };
    }

    /// <summary>Swaps the variant dictionary to match the current mode. When the mode resolves
    /// to "system", picks the dark variant if the runtime variant is dark, otherwise the light one.</summary>
    public void ApplyVariantForMode(string mode, string lightVariantId, string darkVariantId)
    {
        var app = Application.Current;
        if (app is null) return;

        var effective = mode.ToLowerInvariant() switch
        {
            "light" => "light",
            "dark"  => "dark",
            _       => app.ActualThemeVariant == ThemeVariant.Light ? "light" : "dark",
        };

        var variant = effective == "light"
            ? ThemeCatalog.ResolveLight(lightVariantId)
            : ThemeCatalog.ResolveDark(darkVariantId);

        SwapVariantDictionary(variant.ResourceUri);
    }

    /// <summary>Replaces the variant slot with a fresh ResourceInclude pointing at the given
    /// avares URI. We rebuild the include rather than mutating Source on the existing one
    /// because Avalonia caches the loaded dictionary on first access and a Source change
    /// after that doesn't propagate to merged-resource lookups.</summary>
    private static void SwapVariantDictionary(string avaresUri)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var include = new ResourceInclude((Uri?)null) { Source = new Uri(avaresUri) };

        // Pre-load so we surface XAML errors here (caller frame) rather than later
        // when DynamicResource consumers query a missing key.
        _ = include.Loaded;

        if (resources.MergedDictionaries.Count > VariantSlotIndex)
        {
            resources.MergedDictionaries[VariantSlotIndex] = include;
        }
        else
        {
            resources.MergedDictionaries.Add(include);
        }
    }
}
