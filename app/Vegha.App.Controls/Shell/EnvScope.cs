namespace Vegha.App.Controls.Shell;

/// <summary>
/// Identifies which environment scope a UI action targets. Carried on
/// <see cref="AppTopBar.ConfigureEnvsRequested"/> so the host can route Configure to the
/// right surface — collection envs live in the left activity rail panel, global envs in
/// the workspace editor dialog.
/// </summary>
public enum EnvScope
{
    Collection,
    Global,
}
