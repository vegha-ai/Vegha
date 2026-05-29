using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Repaints AvaloniaEdit syntax-highlighting definitions so the editor honors the
/// <em>active</em> theme variant, not just a generic dark/light split. The stock XML /
/// JSON / JavaScript palettes that ship with AvaloniaEdit are tuned for a mid-grey
/// theme and look wrong on both our dark code backgrounds and the themed variants
/// (Nord, Dracula, Solarized, …).
///
/// Each highlight color is mapped — by category name — to one of the per-theme
/// <c>Code*Brush</c> tokens declared in <c>Themes/Tokens/*.axaml</c> and resolved from
/// the application resource dictionary for the current <see cref="ThemeVariant"/>. That
/// makes string/key/number/tag hues come from each theme's own family, so the response
/// body finally feels native to Nord/Dracula/etc. instead of showing VS-Code blue
/// everywhere (audit §07.6).
///
/// On the first call we subscribe once to <see cref="Application.ActualThemeVariantProperty"/>
/// so every definition we've patched is repainted when the user flips theme without
/// restarting. Re-patching the same definition with the same palette is a no-op.
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
        foreach (var color in def.NamedHighlightingColors)
        {
            var key = ChooseBrushKeyByCategory(color.Name);
            if (key is not null && TryGetThemeColor(key, out var c))
            {
                color.Foreground = new SimpleHighlightingBrush(c);
            }
        }
    }

    /// <summary>Category-based name match so we tolerate naming drift across
    /// AvaloniaEdit versions (XmlTag vs Tag vs TagName, XmlAttribute vs
    /// AttributeName, etc.). Each category resolves to one of the per-theme
    /// <c>Code*Brush</c> token keys. Order matters: more specific matches first.
    /// Returns <c>null</c> when no category matches, in which case we leave the
    /// original color alone.</summary>
    private static string? ChooseBrushKeyByCategory(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("comment"))                       return "CodeCommentBrush";
        if (n.Contains("cdata"))                         return "CodeKeywordBrush";
        if (n.Contains("doctype"))                       return "CodeAttrBrush";
        if (n.Contains("declaration") || n.Contains("processinginstruction"))
                                                         return "CodeTagBrush";
        if (n.Contains("attribute") && n.Contains("value"))
                                                         return "CodeAttrValueBrush";
        if (n.Contains("attribute"))                     return "CodeAttrBrush";
        if (n.Contains("tag") || n.Contains("element"))  return "CodeTagBrush";
        if (n.Contains("entity"))                        return "CodeFunctionBrush";
        if (n.Contains("string"))                        return "CodeStringBrush";
        if (n.Contains("digit") || n.Contains("number")) return "CodeNumberBrush";
        if (n.Contains("bool") || n.Contains("null"))    return "CodeBoolBrush";
        if (n.Contains("field") || n.Contains("key") || n.Contains("property"))
                                                         return "CodeAttrBrush";
        if (n.Contains("keyword"))                       return "CodeKeywordBrush";
        if (n.Contains("method") || n.Contains("function") || n.Contains("call"))
                                                         return "CodeFunctionBrush";
        if (n.Contains("punctuation") || n.Contains("symbol") || n.Contains("operator"))
                                                         return "CodePunctBrush";
        return null;
    }

    private static bool TryGetThemeColor(string key, out Color color)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is ISolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        color = default;
        return false;
    }
}
