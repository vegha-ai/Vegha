using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Repaints AvaloniaEdit syntax-highlighting definitions for readability on our
/// dark / light theme variants. The stock XML / JSON / JavaScript palettes that
/// ship with AvaloniaEdit are tuned for a generic mid-grey theme — XML tags
/// render at ~#600000, attribute names at ~#990099, attribute values at navy.
/// Those look fine on light backgrounds but become near-illegible on our dark
/// code background (#0a0c0f).
///
/// On the first call we subscribe once to <see cref="Application.ActualThemeVariantProperty"/>
/// so any definitions we've already patched get repainted when the user flips
/// theme without restarting the app. Each definition is patched ONCE per theme
/// change — re-patching the same definition with the same palette is a no-op
/// for the underlying brushes.
/// </summary>
internal static class EditorSyntaxTheme
{
    private static readonly HashSet<IHighlightingDefinition> Patched = new();
    private static bool _themeListenerHooked;

    /// <summary>Apply the active palette to <paramref name="def"/>. Callers should
    /// invoke this immediately after assigning <c>SyntaxHighlighting</c> on the
    /// editor — both before the first paint and on theme switches.</summary>
    public static void Apply(IHighlightingDefinition? def)
    {
        if (def is null) return;
        HookThemeListenerOnce();
        ApplyPalette(def);
        Patched.Add(def);
    }

    private static void HookThemeListenerOnce()
    {
        if (_themeListenerHooked) return;
        var app = Application.Current;
        if (app is null) return;
        app.PropertyChanged += (_, e) =>
        {
            if (e.Property == Application.ActualThemeVariantProperty)
            {
                foreach (var d in Patched) ApplyPalette(d);
            }
        };
        _themeListenerHooked = true;
    }

    private static void ApplyPalette(IHighlightingDefinition def)
    {
        var isDark = IsDark();
        foreach (var color in def.NamedHighlightingColors)
        {
            var hex = ChooseHexByCategory(color.Name, isDark);
            if (hex is not null && Color.TryParse(hex, out var c))
            {
                color.Foreground = new SimpleHighlightingBrush(c);
            }
        }
    }

    /// <summary>Category-based name match so we tolerate naming drift across
    /// AvaloniaEdit versions (XmlTag vs Tag vs TagName, XmlAttribute vs
    /// AttributeName, etc.). Anything containing "tag" / "element" gets the
    /// "markup element" treatment; "attribute" + "value" gets the value color;
    /// "attribute" alone (no "value") gets the attribute-name color; and so on.
    /// Returns <c>null</c> when no category matches, in which case we leave
    /// the original color alone.</summary>
    private static string? ChooseHexByCategory(string name, bool isDark)
    {
        var n = name.ToLowerInvariant();
        // Order matters: more specific matches must come first.
        if (n.Contains("comment"))                       return isDark ? "#6A9955" : "#008000";
        if (n.Contains("cdata"))                         return isDark ? "#C586C0" : "#6F42C1";
        if (n.Contains("doctype"))                       return isDark ? "#9CDCFE" : "#E50000";
        if (n.Contains("declaration") || n.Contains("processinginstruction"))
                                                         return isDark ? "#569CD6" : "#800000";
        if (n.Contains("attribute") && n.Contains("value"))
                                                         return isDark ? "#CE9178" : "#0451A5";
        if (n.Contains("attribute"))                     return isDark ? "#9CDCFE" : "#E50000";
        if (n.Contains("tag") || n.Contains("element"))  return isDark ? "#569CD6" : "#800000";
        if (n.Contains("brokenentity"))                  return isDark ? "#F48771" : "#C61C1C";
        if (n.Contains("entity"))                        return isDark ? "#D7BA7D" : "#9B4F00";
        if (n.Contains("string"))                        return isDark ? "#CE9178" : "#A31515";
        if (n.Contains("digit") || n.Contains("number")) return isDark ? "#B5CEA8" : "#098658";
        if (n.Contains("bool") || n.Contains("null"))    return isDark ? "#569CD6" : "#0070C9";
        if (n.Contains("field") || n.Contains("key") || n.Contains("property"))
                                                         return isDark ? "#9CDCFE" : "#0070C9";
        if (n.Contains("keyword"))                       return isDark ? "#C586C0" : "#0000FF";
        if (n.Contains("method") || n.Contains("function") || n.Contains("call"))
                                                         return isDark ? "#DCDCAA" : "#795E26";
        if (n.Contains("punctuation") || n.Contains("symbol") || n.Contains("operator"))
                                                         return isDark ? "#D4D4D4" : "#15181D";
        return null;
    }

    private static bool IsDark()
    {
        var app = Application.Current;
        if (app is null) return true; // before-startup default
        return app.ActualThemeVariant == ThemeVariant.Dark
            || app.ActualThemeVariant == ThemeVariant.Default; // honor system-dark
    }

}
