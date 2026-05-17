using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Appearance page VM. Drives the three-button mode selector, the variant card
/// grid (filtered to the current mode), the interface-zoom slider, and the UI font fields.</summary>
public partial class AppearanceSettingsViewModel : SettingsPageBase
{
    public override string Id => "appearance";
    public override string Title => "Appearance";
    public override string IconKey => "Sun";

    public IReadOnlyList<string> Modes { get; } = new[] { "light", "dark", "system" };

    /// <summary>Full variant catalogue (light + dark). The view binds to
    /// <see cref="FilteredVariants"/> for display so the card grid auto-narrows
    /// to the variants compatible with the currently-selected mode.</summary>
    public ObservableCollection<ThemeVariantEntry> AllVariants { get; } = new();

    /// <summary>Variants visible in the card grid: filtered to match the
    /// currently-selected <see cref="ThemeMode"/>. For <c>system</c> we show
    /// both — the user still picks which variant runs in each side, even when
    /// the active side is OS-driven.</summary>
    public ObservableCollection<ThemeVariantEntry> FilteredVariants { get; } = new();

    [ObservableProperty] private string _themeMode = "dark";
    [ObservableProperty] private string _themeVariantLight = "Light";
    [ObservableProperty] private string _themeVariantDark = "Dark";
    [ObservableProperty] private double _interfaceZoom = 1.0;
    [ObservableProperty] private string _fontFamily = "JetBrains Mono";
    [ObservableProperty] private int _fontSize = 12;

    public AppearanceSettingsViewModel()
    {
        // Variant catalogue + their preview swatches. Colors are duplicated from
        // Themes/Tokens/*.axaml so each card can render its OWN palette regardless
        // of which theme is currently active (otherwise every card would look the
        // same — the previous DynamicResource bindings always resolved to the
        // currently-active theme's brushes).
        AllVariants.Add(new ThemeVariantEntry("Light", "Light", "light",
            Background: "#f4f5f7", Surface: "#ffffff", Text: "#15181d", Accent: "#3b82f6"));
        AllVariants.Add(new ThemeVariantEntry("LightPastel", "Light Pastel", "light",
            Background: "#faf6fb", Surface: "#ffffff", Text: "#241a2c", Accent: "#a78bfa"));
        AllVariants.Add(new ThemeVariantEntry("VSCodeLight", "VS Code Light", "light",
            Background: "#f3f3f3", Surface: "#ffffff", Text: "#1f1f1f", Accent: "#0078d4"));
        AllVariants.Add(new ThemeVariantEntry("GitHubLight", "GitHub Light", "light",
            Background: "#f6f8fa", Surface: "#ffffff", Text: "#1f2328", Accent: "#0969da"));
        AllVariants.Add(new ThemeVariantEntry("SolarizedLight", "Solarized Light", "light",
            Background: "#fdf6e3", Surface: "#ffffff", Text: "#073642", Accent: "#268bd2"));
        AllVariants.Add(new ThemeVariantEntry("NordLight", "Nord Light", "light",
            Background: "#e5e9f0", Surface: "#eceff4", Text: "#2e3440", Accent: "#5e81ac"));
        AllVariants.Add(new ThemeVariantEntry("Dark", "Dark", "dark",
            Background: "#111418", Surface: "#161a1f", Text: "#e6e9ef", Accent: "#3b82f6"));
        AllVariants.Add(new ThemeVariantEntry("DarkCatppuccin", "Dark Catppuccin", "dark",
            Background: "#181825", Surface: "#1e1e2e", Text: "#cdd6f4", Accent: "#cba6f7"));
        AllVariants.Add(new ThemeVariantEntry("Nord", "Nord", "dark",
            Background: "#2e3440", Surface: "#3b4252", Text: "#eceff4", Accent: "#88c0d0"));
        AllVariants.Add(new ThemeVariantEntry("Dracula", "Dracula", "dark",
            Background: "#282a36", Surface: "#343746", Text: "#f8f8f2", Accent: "#bd93f9"));
        AllVariants.Add(new ThemeVariantEntry("SolarizedDark", "Solarized Dark", "dark",
            Background: "#002b36", Surface: "#073642", Text: "#eee8d5", Accent: "#268bd2"));

        RebuildFilteredVariants();
    }

    /// <summary>Currently-selected variant id for the active <see cref="ThemeMode"/>.
    /// Cards bind their selection-highlight visibility to equality with this so the
    /// active variant carries a clear accent ring. For <c>system</c> mode we treat
    /// the dark variant as the "active" highlight target — the OS resolves which
    /// side renders, but only one variant is highlighted to avoid ambiguity.</summary>
    public string SelectedVariantId => ThemeMode == "light" ? ThemeVariantLight : ThemeVariantDark;

    partial void OnThemeModeChanged(string value)
    {
        RebuildFilteredVariants();
        OnPropertyChanged(nameof(SelectedVariantId));
    }

    partial void OnThemeVariantLightChanged(string value) => OnPropertyChanged(nameof(SelectedVariantId));
    partial void OnThemeVariantDarkChanged(string value) => OnPropertyChanged(nameof(SelectedVariantId));

    private void RebuildFilteredVariants()
    {
        FilteredVariants.Clear();
        IEnumerable<ThemeVariantEntry> selected = ThemeMode switch
        {
            "light" => AllVariants.Where(v => v.Mode == "light"),
            "dark" => AllVariants.Where(v => v.Mode == "dark"),
            _ => AllVariants,
        };
        foreach (var v in selected) FilteredVariants.Add(v);
    }

    public override void ReadFrom(AppSettings s)
    {
        ThemeMode = s.ThemeMode;
        ThemeVariantLight = s.ThemeVariantLight;
        ThemeVariantDark = s.ThemeVariantDark;
        InterfaceZoom = s.InterfaceZoom;
        FontFamily = s.FontFamily;
        FontSize = s.FontSize;
    }

    public override AppSettings WriteTo(AppSettings e) => e with
    {
        ThemeMode = ThemeMode,
        // Mirror to the legacy Theme field so any code still reading it stays in sync.
        Theme = ThemeMode,
        ThemeVariantLight = ThemeVariantLight,
        ThemeVariantDark = ThemeVariantDark,
        InterfaceZoom = Math.Clamp(InterfaceZoom, 0.8, 2.0),
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "JetBrains Mono" : FontFamily,
        FontSize = Math.Clamp(FontSize, 8, 24),
    };
}

public sealed record ThemeVariantEntry(
    string Id,
    string DisplayName,
    string Mode,
    string Background,
    string Surface,
    string Text,
    string Accent);
