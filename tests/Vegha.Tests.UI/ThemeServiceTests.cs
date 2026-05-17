using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Vegha.App.Services;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Headless tests for <see cref="ThemeService"/>. Avalonia.Headless gives us a
/// real <see cref="Application"/> with App.axaml's resource dictionaries materialized,
/// so we can assert that mode/variant changes touch the right slots — without needing
/// a window server. Native-platform integrations (OS chrome, transparency hints) are
/// out of scope; this layer covers the cross-platform plumbing that breaks silently
/// when someone reorders MergedDictionaries or renames a resource Uri.</summary>
public class ThemeServiceTests
{
    [AvaloniaFact]
    public void ApplyMode_sets_RequestedThemeVariant()
    {
        var svc = CreateService();

        svc.ApplyMode("dark");
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

        svc.ApplyMode("light");
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);

        svc.ApplyMode("system");
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Default);
    }

    [AvaloniaFact]
    public void ApplyVariantForMode_swaps_the_variant_dictionary_in_place()
    {
        var svc = CreateService();

        // Apply Dark first so we have a baseline at the variant slot, then switching to
        // DarkCatppuccin should reuse the same slot rather than append a new one.
        svc.ApplyVariantForMode("dark", lightVariantId: "Light", darkVariantId: "Dark");
        var baselineCount = Application.Current!.Resources.MergedDictionaries.Count;

        svc.ApplyVariantForMode("dark", lightVariantId: "Light", darkVariantId: "DarkCatppuccin");

        Application.Current!.Resources.MergedDictionaries.Count.Should().Be(baselineCount,
            "switching variants must reuse the slot, not stack new dictionaries on top");
    }

    [AvaloniaFact]
    public void Repeated_apply_does_not_leak_merged_dictionaries()
    {
        var svc = CreateService();
        svc.ApplyVariantForMode("dark", lightVariantId: "Light", darkVariantId: "Dark");
        var baseline = Application.Current!.Resources.MergedDictionaries.Count;

        for (var i = 0; i < 25; i++)
            svc.ApplyVariantForMode("dark", lightVariantId: "Light", darkVariantId: "Dark");

        // Re-applying the same variant in a tight loop must not grow MergedDictionaries —
        // otherwise the theme switcher would leak resource includes on every toggle,
        // slowing resolution and bloating memory over a long session.
        Application.Current!.Resources.MergedDictionaries.Count.Should().Be(baseline);
    }

    [AvaloniaFact]
    public void Unknown_variant_id_falls_back_to_first_variant_in_catalog()
    {
        // Apply with an invalid id — ThemeService should resolve to the first variant of
        // the active mode rather than throw or leave the resources slot blank.
        var svc = CreateService();

        Action act = () => svc.ApplyVariantForMode(
            "dark", lightVariantId: "Light", darkVariantId: "NoSuchVariant");

        act.Should().NotThrow();
        // The catalog's first dark variant is "Dark"; assert via the resource Uri because
        // ThemeService doesn't expose the active variant directly. ResourceInclude.Source
        // points at the avares://.../Dark.axaml that ResolveDark returned.
        var slot = Application.Current!.Resources.MergedDictionaries[1] as ResourceInclude;
        slot.Should().NotBeNull("variant slot must be a ResourceInclude after ApplyVariantForMode");
        slot!.Source!.ToString().Should().Contain("Dark.axaml");
    }

    private static ThemeService CreateService()
    {
        // Use a process-private settings dir so test runs don't stomp on the real user
        // settings under ~/Library/Application Support/Vegha. AppSettingsStore picks
        // this up via the VEGHA_SETTINGS_DIR override.
        var tmpDir = Path.Combine(Path.GetTempPath(), "Vegha-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        Environment.SetEnvironmentVariable("VEGHA_SETTINGS_DIR", tmpDir);
        return new ThemeService(new AppSettingsStore());
    }
}
