namespace Vegha.Core.Persistence;

/// <summary>Persisted user-adjustable pane sizes + show/hide state for the main window shell.
/// <c>IsCodegenCollapsed</c> defaults to <c>true</c> so first-launch starts with the codegen
/// panel hidden; the workspace gets the full horizontal real estate. The user opens it via
/// the toggle button next to the Save button.</summary>
public sealed record LayoutSettings(
    double SidebarWidth,
    double RightPanelWidth,
    double ResponsePaneHeight)
{
    /// <summary>True when the right-hand codegen panel should start collapsed. Persisted so the
    /// user's last open/closed choice survives restarts.</summary>
    public bool IsCodegenCollapsed { get; init; } = true;

    /// <summary>Response pane's share of the request + response height (0..1). The split is
    /// proportional so it scales with the window instead of pinning the response to a fixed
    /// pixel height. Supersedes <see cref="ResponsePaneHeight"/>, which older layout.json
    /// files still carry and is kept only so they deserialize.</summary>
    public double ResponsePaneRatio { get; init; } = DefaultResponsePaneRatio;

    /// <summary>True when the user folded the response pane down to its header strip.</summary>
    public bool IsResponseCollapsed { get; init; }

    public const double DefaultResponsePaneRatio = 0.45;
    public const double MinResponsePaneRatio = 0.15;
    public const double MaxResponsePaneRatio = 0.85;

    public static LayoutSettings Default { get; } = new(
        SidebarWidth: 280,
        RightPanelWidth: 320,
        ResponsePaneHeight: 360);
}
