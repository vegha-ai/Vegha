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

    public static LayoutSettings Default { get; } = new(
        SidebarWidth: 280,
        RightPanelWidth: 320,
        ResponsePaneHeight: 360);
}
