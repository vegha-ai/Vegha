using System;
using System.Text;

namespace Vegha.App.Services;

/// <summary>Renders a logical keyboard gesture into the form expected by the host OS.
/// Gestures are authored Windows-style — "Ctrl"/"Shift"/"Alt" words joined with "+"
/// (e.g. "Ctrl+Shift+Tab"). Windows and Linux keep that literal text; macOS swaps in the
/// ⌘/⇧/⌥ symbols and drops the separators, matching platform convention. Non-gesture
/// placeholders such as "(menu)" pass through unchanged.</summary>
public static class ShortcutFormatter
{
    /// <summary>True when running on macOS, where shortcuts render with ⌘ instead of Ctrl.</summary>
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>Format a single logical gesture — e.g. "Ctrl+Shift+Tab" → "⌘⇧Tab" on macOS,
    /// or returned verbatim on Windows / Linux.</summary>
    public static string Format(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || !IsMac)
            return gesture;
        if (gesture.StartsWith('(')) // placeholders like "(menu)"
            return gesture;

        var sb = new StringBuilder(gesture.Length);
        foreach (var part in gesture.Split('+'))
        {
            sb.Append(part.Trim() switch
            {
                "Ctrl" or "Cmd"     => "⌘", // ⌘
                "Shift"             => "⇧", // ⇧
                "Alt"               => "⌥", // ⌥
                "Enter" or "Return" => "↩", // ↩
                var key             => key,
            });
        }
        return sb.ToString();
    }
}
