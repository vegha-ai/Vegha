namespace Vegha.App.Controls.Shell;

/// <summary>
/// Eight-swatch palette used by the environment color picker. Hex values are kept here
/// so the picker UI, the renderer in the top-bar pill, and the row dot stay in sync —
/// changing a color in one place is enough.
/// </summary>
public static class EnvironmentColorPalette
{
    public sealed record Swatch(string Name, string Hex);

    public static readonly IReadOnlyList<Swatch> Swatches = new Swatch[]
    {
        new("Red",    "#DC2626"),
        new("Orange", "#EA580C"),
        new("Yellow", "#CA8A04"),
        new("Green",  "#16A34A"),
        new("Teal",   "#0891B2"),
        new("Blue",   "#2563EB"),
        new("Purple", "#7C3AED"),
        new("Gray",   "#475569"),
    };
}
