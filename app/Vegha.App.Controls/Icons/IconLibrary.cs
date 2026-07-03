using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Vegha.App.Controls.Icons;

/// <summary>Builds the visual children for a given <see cref="IconKind"/>. Each glyph is drawn
/// inside a 24×24 viewBox; the consuming <see cref="Icon"/> control places these in a Viewbox
/// to scale to <c>Size</c>.
///
/// Domain glyphs (Collection, Env, Settings, FlowRunner, Git, Vault, History) load from the
/// embedded SVG resources under <c>Icons/Svg/*.svg</c> via <see cref="SvgIconRenderer"/> —
/// when those files change, the rendered icon updates with no code change. Common UI
/// glyphs (Plus, Close, Folder, etc.) have no SVG file and live as inline geometry here.</summary>
internal static class IconLibrary
{
    public static IEnumerable<Shape> Build(IconKind kind, IBrush stroke)
    {
        switch (kind)
        {
            // --- SVG-backed (Icons/Svg/*.svg) ---
            case IconKind.Collection:
                foreach (var s in SvgIconRenderer.Build("collection.svg", stroke)) yield return s;
                break;
            case IconKind.Env:
            case IconKind.Globe:
                foreach (var s in SvgIconRenderer.Build("env.svg", stroke)) yield return s;
                break;
            case IconKind.Workspace:
                foreach (var s in SvgIconRenderer.Build("workspace.svg", stroke)) yield return s;
                break;
            case IconKind.Settings:
                foreach (var s in SvgIconRenderer.Build("settings.svg", stroke)) yield return s;
                break;
            case IconKind.FlowRunner:
                foreach (var s in SvgIconRenderer.Build("flow_runner.svg", stroke)) yield return s;
                break;
            case IconKind.Git:
                foreach (var s in SvgIconRenderer.Build("git.svg", stroke)) yield return s;
                break;
            case IconKind.Vault:
                foreach (var s in SvgIconRenderer.Build("vault.svg", stroke)) yield return s;
                break;
            case IconKind.History:
                foreach (var s in SvgIconRenderer.Build("history.svg", stroke)) yield return s;
                break;
            case IconKind.OpenApi:
                foreach (var s in SvgIconRenderer.Build("openapi.svg", stroke)) yield return s;
                break;
            case IconKind.Eye:
                foreach (var s in SvgIconRenderer.Build("eye.svg", stroke)) yield return s;
                break;
            case IconKind.Warning:
                foreach (var s in SvgIconRenderer.Build("warning.svg", stroke)) yield return s;
                break;
            case IconKind.DropFile:
                foreach (var s in SvgIconRenderer.Build("DropFile.svg", stroke)) yield return s;
                break;

            // --- Inline geometry (no SVG asset) ---
            case IconKind.Swagger:
                yield return Stroke("M19.5 12 A 7.5 7.5 0 1 0 12 19.5 M12 4.5 C 13.4 6.1 14.25 9 14.25 12 M4.75 9.5 H15.75 M4.75 14.5 H12 M16 16 L18 18 L22 13.5", stroke);
                break;
            case IconKind.Flow:
                yield return Stroke("M3 3.5 H9 V7.5 H3 Z M15 3.5 H21 V7.5 H15 Z M3 16.5 H9 V20.5 H3 Z M15 16.5 H21 V20.5 H15 Z M9 5.5 H15 M9 18.5 H15 M6 7.5 V16.5 M18 7.5 V16.5", stroke);
                break;
            case IconKind.Team:
                yield return StrokeEllipse(9, 9, 3, stroke);
                yield return Stroke("M3 19 C 3 15.7 5.7 13 9 13 C 12.3 13 15 15.7 15 19", stroke);
                yield return StrokeEllipse(17, 7.75, 2.25, stroke);
                yield return Stroke("M17 12.25 C 19.75 12.25 22 14.35 22 17", stroke);
                break;
            case IconKind.Help:
                yield return StrokeEllipse(13.5, 13.5, 9.075, stroke);
                yield return Stroke("M10.725 10.45 A2.475 2.475 0 1 1 13.75 12.87 C13.09 13.09 12.925 13.915 12.925 14.575", stroke);
                yield return Dot(13.2, 18.15, stroke, 0.825);
                break;
            case IconKind.Cookie:
                // Bitten circle with a few "chip" dots inside.
                yield return Stroke("M12 3 A9 9 0 1 0 21 12 A3 3 0 0 1 18 9 A3 3 0 0 1 15 6 A3 3 0 0 1 12 3 Z", stroke);
                yield return Dot(9, 11, stroke, 0.7);
                yield return Dot(13, 13, stroke, 0.7);
                yield return Dot(10, 16, stroke, 0.7);
                break;

            // --- Common ---
            case IconKind.Search:
                yield return StrokeEllipse(10.5, 10.5, 6, stroke);
                yield return Stroke("M15 15 L20 20", stroke);
                break;
            case IconKind.Plus:
                yield return Stroke("M12 5 V19 M5 12 H19", stroke);
                break;
            case IconKind.Minus:
                yield return Stroke("M5 12 H19", stroke);
                break;
            case IconKind.Discard:
                // VSCode-style "discard changes" — counter-clockwise revert arrow.
                yield return Stroke("M4 12 A 8 8 0 1 0 6 6.5 M4 4 V7 H7", stroke);
                break;
            case IconKind.Close:
                yield return Stroke("M6 6 L18 18 M18 6 L6 18", stroke);
                break;
            case IconKind.Menu:
                yield return Stroke("M4 7 H20 M4 12 H20 M4 17 H20", stroke);
                break;
            case IconKind.ChevronRight:
                yield return Stroke("M9 5 L16 12 L9 19", stroke);
                break;
            case IconKind.ChevronDown:
                yield return Stroke("M5 9 L12 16 L19 9", stroke);
                break;
            case IconKind.ChevronUp:
                yield return Stroke("M5 15 L12 8 L19 15", stroke);
                break;
            case IconKind.More:
                yield return Dot(5, 12, stroke, 1.1);
                yield return Dot(12, 12, stroke, 1.1);
                yield return Dot(19, 12, stroke, 1.1);
                break;
            case IconKind.MoreVertical:
                yield return Dot(12, 5, stroke, 1.1);
                yield return Dot(12, 12, stroke, 1.1);
                yield return Dot(12, 19, stroke, 1.1);
                break;
            case IconKind.FileText:
                yield return Stroke("M5 3.5 H14 L19 8.5 V20.5 H5 Z M14 3.5 V8.5 H19 M8 12.5 H16 M8 16 H16 M8 8.5 H11", stroke);
                break;
            case IconKind.Sync:
                // Two arrows in a loop — VSCode-style "synchronize changes".
                yield return Stroke("M19 12 A 7 7 0 0 1 12 19 A 7 7 0 0 1 7.5 17.3 M5 12 A 7 7 0 0 1 12 5 A 7 7 0 0 1 16.5 6.7 M16.5 4 V7 H13.5 M7.5 20 V17 H10.5", stroke);
                break;
            case IconKind.CloudDownload:
                yield return Stroke("M6.5 16.5 A 4 4 0 0 1 7 8.6 A 5.5 5.5 0 0 1 18.5 9.5 A 3.5 3.5 0 0 1 18 16.5 M12 11 V19 M9 16 L12 19 L15 16", stroke);
                break;
            case IconKind.CloudUpload:
                yield return Stroke("M6.5 16.5 A 4 4 0 0 1 7 8.6 A 5.5 5.5 0 0 1 18.5 9.5 A 3.5 3.5 0 0 1 18 16.5 M12 19 V11 M9 14 L12 11 L15 14", stroke);
                break;
            case IconKind.Stash:
                // Stack of layers.
                yield return Stroke("M3 7.5 L12 3.5 L21 7.5 L12 11.5 Z M5 11 L12 14.5 L19 11 M5 14.5 L12 18 L19 14.5 M5 18 L12 21.5 L19 18", stroke);
                break;
            case IconKind.Branch:
                // VSCode's git-branch glyph: two parallel verticals + a connector.
                yield return StrokeEllipse(6, 5, 1.75, stroke);
                yield return StrokeEllipse(6, 19, 1.75, stroke);
                yield return StrokeEllipse(18, 9, 1.75, stroke);
                yield return Stroke("M6 6.75 V17.25 M6 13 A 6 6 0 0 0 12 7 H16.25", stroke);
                break;
            case IconKind.Undo:
                yield return Stroke("M9 7.5 L4 12 L9 16.5 M4 12 H14 A 6 6 0 0 1 20 18", stroke);
                break;
            case IconKind.Folder:
                yield return Stroke("M3 7.25 A 1.75 1.75 0 0 1 4.75 5.5 H8.35 C 8.75 5.5 9.15 5.65 9.45 5.95 L 10.75 7.25 H19.25 C 20.22 7.25 21 8.03 21 9 V18 C 21 18.97 20.22 19.75 19.25 19.75 H4.75 A 1.75 1.75 0 0 1 3 18 V7.25 Z", stroke);
                break;
            case IconKind.FolderOpen:
                yield return Stroke("M3 7.25 A 1.75 1.75 0 0 1 4.75 5.5 H8.35 C 8.75 5.5 9.15 5.65 9.45 5.95 L 10.75 7.25 H19.25 C 20.22 7.25 21 8.03 21 9 V11", stroke);
                yield return Stroke("M2.5 19.75 L4.7 12.4 C 4.88 11.8 5.44 11.4 6.06 11.4 H22 L 19.8 18.75 C 19.62 19.35 19.06 19.75 18.44 19.75 H2.5 Z", stroke);
                break;
            case IconKind.Download:
                yield return Stroke("M12 3 V15 M6.5 9.5 L12 15 L17.5 9.5 M4 20 H20", stroke);
                break;
            case IconKind.Upload:
                yield return Stroke("M12 16 V4 M6.5 9.5 L12 4 L17.5 9.5 M4 20 H20", stroke);
                break;
            case IconKind.Send:
                yield return Stroke("M21.5 12 L3 4 L5.5 12 L3 20 L21.5 12 Z M5.5 12 H21.5", stroke);
                break;
            case IconKind.Refresh:
                yield return Stroke("M3.5 12 A 8.5 8.5 0 0 1 18 6 L20.5 8.5 M20.5 12 A 8.5 8.5 0 0 1 6 18 L3.5 15.5 M20.5 3.5 V8.5 H15.5 M3.5 20.5 V15.5 H8.5", stroke);
                break;
            case IconKind.Filter:
                yield return Stroke("M4 5 H20 L 13.75 12.5 V19 L 10.25 17.25 V12.5 L 4 5 Z", stroke);
                break;
            case IconKind.Save:
                yield return Stroke("M4.75 4.75 A 1.75 1.75 0 0 1 6.5 3 H16.25 L20.25 7 V19.25 C 20.25 20.22 19.47 21 18.5 21 H6.5 A 1.75 1.75 0 0 1 4.75 19.25 V4.75 Z M7.5 3 V7.5 H16 V3 M7.5 13.5 H16.5 V21 H7.5 Z", stroke);
                break;
            case IconKind.Bell:
                yield return Stroke("M6 10 A 6 6 0 1 1 18 10 V13.5 L19.75 16 H4.25 L 6 13.5 V10 Z M9.5 18.5 A 2.5 2.5 0 0 0 14.5 18.5", stroke);
                break;
            case IconKind.Copy:
                yield return Stroke("M8.25 8.25 H20.25 V20.25 H8.25 Z M5 15.75 H4.75 A 1.75 1.75 0 0 1 3 14 V4.75 C 3 3.78 3.78 3 4.75 3 H14 A 1.75 1.75 0 0 1 15.75 4.75 V5", stroke);
                break;
            case IconKind.Trash:
                yield return Stroke("M4 6.5 H20 M9.5 6.5 V5 A 1.5 1.5 0 0 1 11 3.5 H13 A 1.5 1.5 0 0 1 14.5 5 V6.5 M6.5 6.5 L7.35 18.6 A 1.75 1.75 0 0 0 9.1 20.25 H14.9 A 1.75 1.75 0 0 0 16.65 18.6 L17.5 6.5 M10 10.5 V16.5 M14 10.5 V16.5", stroke);
                break;
            case IconKind.Play:
                yield return Stroke("M7 4.5 V19.5 L20 12 L7 4.5 Z", stroke);
                break;
            case IconKind.Pencil:
                yield return Stroke("M4.5 15.5 L15.5 4.5 A 2.12 2.12 0 0 1 18.5 7.5 L7.5 18.5 L3.5 19.75 L4.5 15.5 Z M13.5 6.5 L16.5 9.5", stroke);
                break;

            // --- Theme ---
            case IconKind.Sun:
                yield return StrokeEllipse(12, 12, 3.5, stroke);
                yield return Stroke("M12 2.75 V4.75 M12 19.25 V21.25 M21.25 12 H19.25 M4.75 12 H2.75 M18.6 5.4 L17.2 6.8 M6.8 17.2 L5.4 18.6 M18.6 18.6 L17.2 17.2 M6.8 6.8 L5.4 5.4", stroke);
                break;
            case IconKind.Moon:
                yield return Stroke("M20.5 14 A 8.5 8.5 0 0 1 9.5 3 A 8.5 8.5 0 1 0 20.5 14 Z", stroke);
                break;

            // <> code brackets
            case IconKind.Code:
                yield return Stroke("M8.5 7.5 L4 12 L8.5 16.5 M15.5 7.5 L20 12 L15.5 16.5", stroke);
                break;

            // Stylized keyboard outline
            case IconKind.Keyboard:
                yield return Stroke("M3 7.5 H21 V16.5 H3 Z M6 10.5 H6.5 M9 10.5 H9.5 M12 10.5 H12.5 M15 10.5 H15.5 M18 10.5 H18.5 M6 13.5 H8 M10 13.5 H14 M16 13.5 H18", stroke);
                break;
        }
    }

    private static global::Avalonia.Controls.Shapes.Path Stroke(string data, IBrush brush) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = 1.5,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        Fill = null,
    };

    private static Ellipse StrokeEllipse(double cx, double cy, double r, IBrush brush) => new()
    {
        Width = r * 2,
        Height = r * 2,
        Stroke = brush,
        StrokeThickness = 1.5,
        Fill = null,
        Margin = new global::Avalonia.Thickness(cx - r, cy - r, 0, 0),
    };

    private static Ellipse Dot(double cx, double cy, IBrush brush, double r = 0.9) => new()
    {
        Width = r * 2,
        Height = r * 2,
        Fill = brush,
        Margin = new global::Avalonia.Thickness(cx - r, cy - r, 0, 0),
    };
}
