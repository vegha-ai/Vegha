using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Vegha.Core.Scripting;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Draws red wavy underlines under script syntax errors. An <see cref="IBackgroundRenderer"/>
/// added to the editor's <c>TextView.BackgroundRenderers</c>; it paints a zig-zag stroke along
/// the bottom of each diagnostic span. Spanning is delegated to
/// <see cref="BackgroundGeometryBuilder.GetRectsForSegment"/> so wrapped lines are handled.
/// </summary>
internal sealed class SquiggleRenderer : IBackgroundRenderer
{
    private readonly List<ScriptDiagnostic> _diagnostics = new();
    private readonly Pen _pen;

    public SquiggleRenderer(IBrush color)
    {
        _pen = new Pen(color, 1.1);
    }

    // Draw above the line background but below text/caret — Selection layer is the conventional spot.
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Replaces the current diagnostics and requests a repaint of the layer.</summary>
    public void SetDiagnostics(IReadOnlyList<ScriptDiagnostic> diagnostics, TextView textView)
    {
        _diagnostics.Clear();
        _diagnostics.AddRange(diagnostics);
        textView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_diagnostics.Count == 0 || !textView.VisualLinesValid) return;
        var docLength = textView.Document?.TextLength ?? 0;

        foreach (var d in _diagnostics)
        {
            if (d.Offset >= docLength) continue;
            var length = Math.Min(d.Length, docLength - d.Offset);
            if (length < 1) length = 1;

            var segment = new TextSegment { StartOffset = d.Offset, Length = length };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                DrawWavyUnderline(drawingContext, rect.Left, rect.Right, rect.Bottom - 1);
            }
        }
    }

    private void DrawWavyUnderline(DrawingContext ctx, double left, double right, double y)
    {
        const double step = 3.0;     // horizontal distance per half-wave
        const double amplitude = 1.6; // peak-to-baseline height

        if (right - left < step) right = left + step;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(left, y), isFilled: false);
            var up = true;
            for (var x = left + step; x <= right; x += step)
            {
                g.LineTo(new Point(x, up ? y - amplitude : y));
                up = !up;
            }
        }
        ctx.DrawGeometry(null, _pen, geometry);
    }
}
