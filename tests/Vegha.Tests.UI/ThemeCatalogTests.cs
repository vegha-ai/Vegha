using Vegha.App.Services;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Pure-logic tests for <see cref="ThemeCatalog"/>. No Avalonia runtime needed
/// — the catalog is just metadata. Lives in the UI test project so it stays co-located
/// with the headless integration tests that exercise the same variants end-to-end via
/// <see cref="ThemeService"/>.</summary>
public class ThemeCatalogTests
{
    [Fact]
    public void ResolveDark_falls_back_to_first_variant_for_unknown_id()
    {
        var v = ThemeCatalog.ResolveDark("DoesNotExist");
        v.Id.Should().Be(ThemeCatalog.DarkVariants[0].Id);
    }

    [Fact]
    public void ResolveLight_falls_back_to_first_variant_for_unknown_id()
    {
        var v = ThemeCatalog.ResolveLight("DoesNotExist");
        v.Id.Should().Be(ThemeCatalog.LightVariants[0].Id);
    }

    [Fact]
    public void Resolve_returns_matching_variant_when_id_is_known()
    {
        ThemeCatalog.ResolveDark("DarkCatppuccin").Id.Should().Be("DarkCatppuccin");
        ThemeCatalog.ResolveLight("VSCodeLight").Id.Should().Be("VSCodeLight");
    }

    [Fact]
    public void Every_variant_has_a_loadable_avares_uri()
    {
        // The avares:// URI is what ThemeService passes to ResourceInclude; a malformed
        // value would fail silently later when DynamicResource lookups miss. Sanity-check
        // the catalog contract up front so a typo in a Variant ctor is caught here.
        foreach (var v in ThemeCatalog.LightVariants.Concat(ThemeCatalog.DarkVariants))
        {
            v.ResourceUri.Should().StartWith("avares://", $"{v.Id} should use avares scheme");
            v.Mode.Should().BeOneOf("light", "dark");
            v.Id.Should().NotBeNullOrWhiteSpace();
            v.DisplayName.Should().NotBeNullOrWhiteSpace();
        }
    }
}
