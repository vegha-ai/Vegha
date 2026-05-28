using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;

namespace Vegha.App.Controls.Icons;

/// <summary>
/// Loads an SVG file embedded as <c>AvaloniaResource</c> under
/// <c>Icons/Svg/&lt;name&gt;.svg</c> and produces a list of Avalonia <see cref="Shape"/> objects
/// the <see cref="Icon"/> control drops onto its 24×24 canvas.
///
/// Scope is intentionally narrow — just the elements our hand-authored icons use:
/// <c>&lt;rect&gt;</c> (with <c>rx</c>), <c>&lt;circle&gt;</c>, <c>&lt;ellipse&gt;</c>, <c>&lt;path&gt;</c>. Each shape inherits
/// fill/stroke from the &lt;svg&gt; root and may override via attribute or inline
/// <c>style</c>. The only transform we honor is <c>transform="scale(1,-1)"</c> on
/// <c>&lt;circle&gt;</c> (Inkscape uses it routinely) — applied by negating <c>cy</c> at parse.
///
/// Concrete fill colors like <c>#000000</c> (Inkscape's default) are remapped to the
/// caller-provided <paramref name="foreground"/> brush so a single SVG renders correctly
/// in light + dark themes.
/// </summary>
internal static class SvgIconRenderer
{
    private static readonly ConcurrentDictionary<string, XDocument> s_cache = new();

    public static IEnumerable<Shape> Build(string svgFileName, IBrush foreground)
    {
        var doc = LoadDocument(svgFileName);
        if (doc?.Root is not { } root) yield break;

        var rootFill = root.Attribute("fill")?.Value ?? "none";
        var rootStroke = root.Attribute("stroke")?.Value ?? "currentColor";
        var rootStrokeWidth = ParseDouble(root.Attribute("stroke-width")?.Value, 1.5);

        // Normalize the SVG's coordinate system into the renderer's 24×24 target. Hand-
        // authored 24×24 icons (collection.svg etc.) produce an identity transform and
        // render unchanged; Inkscape exports with a millimetre viewBox + wrapping
        // <g translate(...)> are scaled + offset to fit. Computed once here and stamped
        // onto every emitted Shape via RenderTransform so we don't have to rewrite path
        // d-strings.
        var normalizer = ComputeNormalizingTransform(root);

        foreach (var el in root.Elements())
        {
            // Skip <defs>, <sodipodi:namedview>, etc. — anything not a shape.
            switch (el.Name.LocalName)
            {
                case "rect":
                {
                    if (BuildRect(el, foreground, rootFill, rootStroke, rootStrokeWidth) is { } s)
                    { ApplyTransform(s, normalizer); yield return s; }
                    break;
                }
                case "circle":
                {
                    if (BuildCircle(el, foreground, rootFill, rootStroke, rootStrokeWidth) is { } s)
                    { ApplyTransform(s, normalizer); yield return s; }
                    break;
                }
                case "ellipse":
                {
                    if (BuildEllipse(el, foreground, rootFill, rootStroke, rootStrokeWidth) is { } s)
                    { ApplyTransform(s, normalizer); yield return s; }
                    break;
                }
                case "path":
                {
                    if (BuildPath(el, foreground, rootFill, rootStroke, rootStrokeWidth) is { } s)
                    { ApplyTransform(s, normalizer); yield return s; }
                    break;
                }
                case "g":
                {
                    // Flatten one level of grouping. Real-world Inkscape files occasionally wrap
                    // shapes in a <g>; we treat group-level fill/stroke as inherited from root
                    // (we don't carry per-group overrides since none of our icons need them).
                    // The group's own transform is rolled into `normalizer` above, so we don't
                    // re-apply it here.
                    foreach (var inner in el.Elements())
                    {
                        Shape? s = inner.Name.LocalName switch
                        {
                            "rect" => BuildRect(inner, foreground, rootFill, rootStroke, rootStrokeWidth),
                            "circle" => BuildCircle(inner, foreground, rootFill, rootStroke, rootStrokeWidth),
                            "ellipse" => BuildEllipse(inner, foreground, rootFill, rootStroke, rootStrokeWidth),
                            "path" => BuildPath(inner, foreground, rootFill, rootStroke, rootStrokeWidth),
                            _ => null,
                        };
                        if (s is not null) { ApplyTransform(s, normalizer); yield return s; }
                    }
                    break;
                }
            }
        }
    }

    private static void ApplyTransform(Shape s, ITransform? transform)
    {
        if (transform is null) return;
        s.RenderTransform = transform;
    }

    /// <summary>Builds an affine transform that maps the SVG's authored coordinate space into
    /// the renderer's 24×24 target Canvas. Maps `viewBox` + first-level `<g transform="translate(...)">`
    /// — that's the shape Inkscape's exporter produces (the canvas is sized in mm/in and shapes
    /// live inside a translated layer group). Returns null when the SVG is already in 24×24 space
    /// with no group offset (the existing hand-authored icons), so we avoid attaching a no-op
    /// transform to every shape.</summary>
    private static ITransform? ComputeNormalizingTransform(XElement root)
    {
        var (vbX, vbY, vbW, vbH) = ParseViewBox(root.Attribute("viewBox")?.Value);
        if (vbW <= 0 || vbH <= 0) return null;

        var firstG = root.Elements().FirstOrDefault(e => e.Name.LocalName == "g");
        var (gtX, gtY) = ParseTranslate(firstG?.Attribute("transform")?.Value);

        // Uniform scale, centered — preserves aspect ratio for non-square viewBoxes.
        var scale = System.Math.Min(24.0 / vbW, 24.0 / vbH);
        var centerOffsetX = (24.0 - vbW * scale) / 2.0;
        var centerOffsetY = (24.0 - vbH * scale) / 2.0;

        // Final point map: (p + gTranslate - vbOrigin) * scale + centerOffset.
        // Expanded: p * scale + (scale * (gTranslate - vbOrigin) + centerOffset).
        var m = new Matrix(
            scale, 0,
            0, scale,
            scale * (gtX - vbX) + centerOffsetX,
            scale * (gtY - vbY) + centerOffsetY);
        if (m.IsIdentity) return null;
        return new MatrixTransform(m);
    }

    private static (double X, double Y, double W, double H) ParseViewBox(string? s)
    {
        if (string.IsNullOrEmpty(s)) return (0, 0, 0, 0);
        var parts = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return (0, 0, 0, 0);
        return (
            ParseDouble(parts[0], 0),
            ParseDouble(parts[1], 0),
            ParseDouble(parts[2], 0),
            ParseDouble(parts[3], 0));
    }

    /// <summary>Pulls a <c>translate(x, y)</c> or <c>translate(x y)</c> out of an SVG transform
    /// attribute. We only support translate (rotate / matrix / scale aren't used by the icons
    /// we ship) — Inkscape's layer wrappers are translate-only in practice.</summary>
    private static readonly Regex s_translateRegex = new(
        @"translate\(\s*(-?[\d.]+)\s*[,\s]\s*(-?[\d.]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static (double X, double Y) ParseTranslate(string? transform)
    {
        if (string.IsNullOrEmpty(transform)) return (0, 0);
        var m = s_translateRegex.Match(transform);
        if (!m.Success) return (0, 0);
        return (
            ParseDouble(m.Groups[1].Value, 0),
            ParseDouble(m.Groups[2].Value, 0));
    }

    private static XDocument? LoadDocument(string svgFileName)
    {
        return s_cache.GetOrAdd(svgFileName, name =>
        {
            var uri = new Uri($"avares://Vegha.App.Controls/Icons/Svg/{name}");
            using var stream = AssetLoader.Open(uri);
            return XDocument.Load(stream);
        });
    }

    // ---------- shape builders ----------

    private static Shape? BuildRect(XElement el, IBrush foreground,
        string rootFill, string rootStroke, double rootStrokeWidth)
    {
        var x = ParseDouble(el.Attribute("x")?.Value, 0);
        var y = ParseDouble(el.Attribute("y")?.Value, 0);
        var w = ParseDouble(el.Attribute("width")?.Value, 0);
        var h = ParseDouble(el.Attribute("height")?.Value, 0);
        var rx = ParseDouble(el.Attribute("rx")?.Value, 0);

        // Express the rect as a path so it sits in the Canvas at absolute coordinates
        // (matching the SVG viewBox), the same way the rest of IconLibrary works.
        var d = ToRoundedRectPath(x, y, w, h, rx);
        return MakePath(d, ResolveFill(el, foreground, rootFill), ResolveStroke(el, foreground, rootStroke),
                        ResolveStrokeWidth(el, rootStrokeWidth));
    }

    private static Shape? BuildCircle(XElement el, IBrush foreground,
        string rootFill, string rootStroke, double rootStrokeWidth)
    {
        var cx = ParseDouble(el.Attribute("cx")?.Value, 0);
        var cy = ParseDouble(el.Attribute("cy")?.Value, 0);
        var r = ParseDouble(el.Attribute("r")?.Value, 0);

        // Honor scale(1,-1) by flipping cy. None of our icons use other transforms.
        if ((el.Attribute("transform")?.Value ?? "").Contains("scale(1,-1)", StringComparison.Ordinal))
            cy = -cy;

        // Two arcs (each half) form a closed circle as a path — keeps the shape kind
        // uniform with rect/path so the consumer doesn't need to special-case Ellipse.
        var d = $"M {Fmt(cx - r)} {Fmt(cy)} a {Fmt(r)} {Fmt(r)} 0 1 0 {Fmt(2 * r)} 0 a {Fmt(r)} {Fmt(r)} 0 1 0 {Fmt(-2 * r)} 0 Z";
        return MakePath(d, ResolveFill(el, foreground, rootFill), ResolveStroke(el, foreground, rootStroke),
                        ResolveStrokeWidth(el, rootStrokeWidth));
    }

    private static Shape? BuildEllipse(XElement el, IBrush foreground,
        string rootFill, string rootStroke, double rootStrokeWidth)
    {
        var cx = ParseDouble(el.Attribute("cx")?.Value, 0);
        var cy = ParseDouble(el.Attribute("cy")?.Value, 0);
        var rx = ParseDouble(el.Attribute("rx")?.Value, 0);
        var ry = ParseDouble(el.Attribute("ry")?.Value, 0);

        // Inkscape sometimes flips the y-axis. None of our shipped icons use it on an
        // ellipse today, but mirror the same handling as BuildCircle for parity.
        if ((el.Attribute("transform")?.Value ?? "").Contains("scale(1,-1)", StringComparison.Ordinal))
            cy = -cy;

        // Express the ellipse as two SVG arcs forming a closed shape — same approach used by
        // BuildCircle so the consumer doesn't need to special-case Ellipse.
        var d = $"M {Fmt(cx - rx)} {Fmt(cy)} a {Fmt(rx)} {Fmt(ry)} 0 1 0 {Fmt(2 * rx)} 0 a {Fmt(rx)} {Fmt(ry)} 0 1 0 {Fmt(-2 * rx)} 0 Z";
        return MakePath(d, ResolveFill(el, foreground, rootFill), ResolveStroke(el, foreground, rootStroke),
                        ResolveStrokeWidth(el, rootStrokeWidth));
    }

    private static Shape? BuildPath(XElement el, IBrush foreground,
        string rootFill, string rootStroke, double rootStrokeWidth)
    {
        var d = el.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(d)) return null;
        return MakePath(d!, ResolveFill(el, foreground, rootFill), ResolveStroke(el, foreground, rootStroke),
                        ResolveStrokeWidth(el, rootStrokeWidth));
    }

    private static global::Avalonia.Controls.Shapes.Path MakePath(string data, IBrush? fill, IBrush? stroke, double strokeWidth)
    {
        return new global::Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = strokeWidth,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
        };
    }

    // ---------- attribute resolution ----------

    private static IBrush? ResolveFill(XElement el, IBrush foreground, string rootFill)
    {
        var direct = el.Attribute("fill")?.Value;
        var styled = StyleValue(el.Attribute("style")?.Value, "fill");
        var v = direct ?? styled ?? rootFill;
        return ToBrush(v, foreground);
    }

    private static IBrush? ResolveStroke(XElement el, IBrush foreground, string rootStroke)
    {
        var direct = el.Attribute("stroke")?.Value;
        var styled = StyleValue(el.Attribute("style")?.Value, "stroke");
        var v = direct ?? styled ?? rootStroke;
        return ToBrush(v, foreground);
    }

    private static double ResolveStrokeWidth(XElement el, double rootWidth)
    {
        var direct = el.Attribute("stroke-width")?.Value;
        var styled = StyleValue(el.Attribute("style")?.Value, "stroke-width");
        // Inkscape inlines tiny stroke-widths (e.g., 0.08) to compensate for filled paths.
        // Those should NOT win — the icon set is designed around the SVG-root 1.5 px width
        // for stroked elements. Honor an explicit override only if it's plausible (≥ 0.5).
        var override_ = direct ?? styled;
        if (override_ is not null && double.TryParse(override_, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w >= 0.5)
            return w;
        return rootWidth;
    }

    private static IBrush? ToBrush(string? value, IBrush foreground)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        // currentColor and any explicit color (Inkscape writes #000000 even on themed icons)
        // both map to the foreground brush so the icon recolors with the theme.
        return foreground;
    }

    /// <summary>Pulls a single CSS-style declaration (e.g., <c>fill</c>) out of an inline
    /// <c>style="a: 1; b: foo"</c> attribute. Returns null when the key isn't present.</summary>
    private static string? StyleValue(string? style, string key)
    {
        if (string.IsNullOrEmpty(style)) return null;
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf(':');
            if (idx <= 0) continue;
            var k = part[..idx].Trim();
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim();
        }
        return null;
    }

    // ---------- helpers ----------

    private static string ToRoundedRectPath(double x, double y, double w, double h, double rx)
    {
        if (rx <= 0)
            return $"M {Fmt(x)} {Fmt(y)} H {Fmt(x + w)} V {Fmt(y + h)} H {Fmt(x)} Z";
        var ry = rx;
        return string.Format(CultureInfo.InvariantCulture,
            "M {0} {1} H {2} A {3} {4} 0 0 1 {5} {6} V {7} A {3} {4} 0 0 1 {2} {8} H {0} A {3} {4} 0 0 1 {9} {7} V {6} A {3} {4} 0 0 1 {0} {1} Z",
            Fmt(x + rx), Fmt(y),                 // 0,1
            Fmt(x + w - rx),                     // 2
            Fmt(rx), Fmt(ry),                    // 3,4
            Fmt(x + w), Fmt(y + ry),             // 5,6
            Fmt(y + h - ry),                     // 7
            Fmt(y + h),                          // 8
            Fmt(x));                             // 9
    }

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
