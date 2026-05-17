namespace Vegha.App.Services;

/// <summary>Static registry of theme variants. Each entry maps a stable Id (persisted in
/// AppSettings) to a display name and the avares:// path of the resource dictionary.
/// Light and dark variants are listed separately because the user picks one of each
/// — the active variant is determined by the current ThemeMode.</summary>
public static class ThemeCatalog
{
    public sealed record Variant(string Id, string DisplayName, string ResourceUri, string Mode);

    public static IReadOnlyList<Variant> LightVariants { get; } = new[]
    {
        new Variant("Light", "Light",
            "avares://Vegha.App/Themes/Tokens/Light.axaml", "light"),
        new Variant("LightPastel", "Light Pastel",
            "avares://Vegha.App/Themes/Tokens/LightPastel.axaml", "light"),
        new Variant("VSCodeLight", "VS Code Light",
            "avares://Vegha.App/Themes/Tokens/VSCodeLight.axaml", "light"),
        new Variant("GitHubLight", "GitHub Light",
            "avares://Vegha.App/Themes/Tokens/GitHubLight.axaml", "light"),
        new Variant("SolarizedLight", "Solarized Light",
            "avares://Vegha.App/Themes/Tokens/SolarizedLight.axaml", "light"),
        new Variant("NordLight", "Nord Light",
            "avares://Vegha.App/Themes/Tokens/NordLight.axaml", "light"),
    };

    public static IReadOnlyList<Variant> DarkVariants { get; } = new[]
    {
        new Variant("Dark", "Dark",
            "avares://Vegha.App/Themes/Tokens/Dark.axaml", "dark"),
        new Variant("DarkCatppuccin", "Dark Catppuccin",
            "avares://Vegha.App/Themes/Tokens/DarkCatppuccin.axaml", "dark"),
        new Variant("Nord", "Nord",
            "avares://Vegha.App/Themes/Tokens/Nord.axaml", "dark"),
        new Variant("Dracula", "Dracula",
            "avares://Vegha.App/Themes/Tokens/Dracula.axaml", "dark"),
        new Variant("SolarizedDark", "Solarized Dark",
            "avares://Vegha.App/Themes/Tokens/SolarizedDark.axaml", "dark"),
    };

    public static Variant ResolveLight(string id) =>
        LightVariants.FirstOrDefault(v => v.Id == id) ?? LightVariants[0];

    public static Variant ResolveDark(string id) =>
        DarkVariants.FirstOrDefault(v => v.Id == id) ?? DarkVariants[0];
}
