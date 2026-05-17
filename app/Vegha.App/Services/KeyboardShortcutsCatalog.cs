using System.Collections.Generic;
using System.Linq;
using Vegha.App.ViewModels.Settings;

namespace Vegha.App.Services;

/// <summary>Read-only catalog of keyboard shortcuts shown on the Settings → Shortcuts page.
/// Gestures are authored once, Windows-style; <see cref="ShortcutFormatter"/> renders them
/// for the running OS (⌘/⇧/⌥ on macOS, literal Ctrl/Shift/Alt on Windows and Linux).
/// Hardcoded for now — when shortcuts become user-rebindable, this catalog will be replaced
/// by a centralized KeybindingRegistry and the Shortcuts page UI stays as-is.</summary>
public static class KeyboardShortcutsCatalog
{
    // (category, action, logical gesture) — gesture is OS-formatted when rows are built.
    private static readonly (string Category, string Action, string Gesture)[] Definitions =
    {
        ("File",    "New request",         "Ctrl+T"),
        ("File",    "Open collection…",    "Ctrl+O"),
        ("File",    "Import…",             "Ctrl+I"),
        ("File",    "Settings…",           "Ctrl+,"),

        ("Edit",    "Save request",        "Ctrl+S"),
        ("Edit",    "Find request",        "Ctrl+K"),

        ("View",    "Zoom in",             "Ctrl+="),
        ("View",    "Zoom out",            "Ctrl+-"),
        ("View",    "Reset zoom",          "Ctrl+0"),
        ("View",    "Toggle theme",        "(menu)"),
        ("View",    "Toggle code panel",   "(menu)"),

        ("Request", "Send request",        "Ctrl+Enter"),
        ("Request", "Switch tab next",     "Ctrl+Tab"),
        ("Request", "Switch tab previous", "Ctrl+Shift+Tab"),
        ("Request", "Close active tab",    "Ctrl+W"),
    };

    public static IReadOnlyList<ShortcutRow> All { get; } = Definitions
        .Select(d => new ShortcutRow(d.Category, d.Action, ShortcutFormatter.Format(d.Gesture)))
        .ToList();
}
