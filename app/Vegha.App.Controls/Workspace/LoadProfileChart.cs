using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Postman-style performance load-profile chart. Renders the three-phase ramp shape —
/// hold at <see cref="InitialLoad"/>, linear ramp to <see cref="TargetVus"/>, then hold at
/// target — as a filled area graph, with two draggable markers the user drags to set the
/// phase boundaries (<see cref="RampStartFraction"/> / <see cref="RampEndFraction"/>, both
/// fractions of the total test duration and two-way bound). The ramp is always a strict
/// sub-segment of the test, so ramp-up time is guaranteed to be less than the total duration.
/// </summary>
public sealed class LoadProfileChart : Control
{
    // Largest share of the test the ramp segment may occupy — keeps ramp-up strictly below the
    // total duration (a hold phase always remains).
    private const double MaxRampSpan = 0.95;
    private const double MinPhase = 0.02;

    public static readonly StyledProperty<int> TargetVusProperty =
        AvaloniaProperty.Register<LoadProfileChart, int>(nameof(TargetVus), 20);
    public static readonly StyledProperty<int> InitialLoadProperty =
        AvaloniaProperty.Register<LoadProfileChart, int>(nameof(InitialLoad), 5);
    public static readonly StyledProperty<double> TotalMinutesProperty =
        AvaloniaProperty.Register<LoadProfileChart, double>(nameof(TotalMinutes), 10);
    public static readonly StyledProperty<double> RampStartFractionProperty =
        AvaloniaProperty.Register<LoadProfileChart, double>(nameof(RampStartFraction), 0.333,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> RampEndFractionProperty =
        AvaloniaProperty.Register<LoadProfileChart, double>(nameof(RampEndFraction), 0.666,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public int TargetVus { get => GetValue(TargetVusProperty); set => SetValue(TargetVusProperty, value); }
    public int InitialLoad { get => GetValue(InitialLoadProperty); set => SetValue(InitialLoadProperty, value); }
    public double TotalMinutes { get => GetValue(TotalMinutesProperty); set => SetValue(TotalMinutesProperty, value); }
    public double RampStartFraction { get => GetValue(RampStartFractionProperty); set => SetValue(RampStartFractionProperty, value); }
    public double RampEndFraction { get => GetValue(RampEndFractionProperty); set => SetValue(RampEndFractionProperty, value); }

    static LoadProfileChart()
    {
        AffectsRender<LoadProfileChart>(
            TargetVusProperty, InitialLoadProperty, TotalMinutesProperty,
            RampStartFractionProperty, RampEndFractionProperty);
    }

    // Plot insets (space for axis labels).
    private const double PadLeft = 46, PadTop = 22, PadRight = 14, PadBottom = 22;

    private enum DragTarget { None, Start, End }
    private DragTarget _drag = DragTarget.None;

    private Rect Plot => new(PadLeft, PadTop,
        Math.Max(1, Bounds.Width - PadLeft - PadRight),
        Math.Max(1, Bounds.Height - PadTop - PadBottom));

    private double XAt(double fraction) => Plot.X + Math.Clamp(fraction, 0, 1) * Plot.Width;

    /// <summary>Y for a VU value on a 0..(target with headroom) axis (top = high load).</summary>
    private double YAt(double vus)
    {
        var max = Math.Max(1, TargetVus) * 1.12; // headroom above the peak
        var t = Math.Clamp(vus / max, 0, 1);
        return Plot.Bottom - t * Plot.Height;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var plot = Plot;

        var axis = new Pen(new SolidColorBrush(Color.FromArgb(90, 140, 150, 160)), 1);
        var line = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0x6E)), 2);
        var dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, 120, 130, 140)), 1)
        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        var fill = new SolidColorBrush(Color.FromArgb(40, 76, 154, 110));
        var textBrush = new SolidColorBrush(Color.FromArgb(200, 130, 140, 150));
        var markerBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6C, 0xF7));

        var start = Math.Clamp(RampStartFraction, 0, 1);
        var end = Math.Clamp(RampEndFraction, start, 1);

        double xStart = XAt(start), xEnd = XAt(end);
        double yInit = YAt(InitialLoad), yTarget = YAt(TargetVus);

        // Filled area under the load line.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(plot.X, plot.Bottom), true);
            g.LineTo(new Point(plot.X, yInit));
            g.LineTo(new Point(xStart, yInit));
            g.LineTo(new Point(xEnd, yTarget));
            g.LineTo(new Point(plot.Right, yTarget));
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, geo);

        // Load line (initial hold → ramp → target hold).
        ctx.DrawLine(line, new Point(plot.X, yInit), new Point(xStart, yInit));
        ctx.DrawLine(line, new Point(xStart, yInit), new Point(xEnd, yTarget));
        ctx.DrawLine(line, new Point(xEnd, yTarget), new Point(plot.Right, yTarget));

        // Axis (left + bottom).
        ctx.DrawLine(axis, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        ctx.DrawLine(axis, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Phase-boundary markers.
        foreach (var x in new[] { xStart, xEnd })
        {
            ctx.DrawLine(dashed, new Point(x, plot.Y - 6), new Point(x, plot.Bottom));
            DrawHandle(ctx, markerBrush, x, plot.Y - 6);
        }

        // Labels.
        DrawText(ctx, $"{TargetVus} VUs", new Point(6, plot.Y - 6), textBrush, 11);
        DrawText(ctx, "0", new Point(plot.X - 12, plot.Bottom + 2), textBrush, 11);
        DrawText(ctx, "0 min", new Point(plot.X, plot.Bottom + 4), textBrush, 10);
        var dur = FormatMinutes(TotalMinutes);
        var right = new FormattedText(dur, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10, textBrush);
        DrawText(ctx, dur, new Point(plot.Right - right.Width, plot.Bottom + 4), textBrush, 10);
    }

    private static void DrawHandle(DrawingContext ctx, IBrush brush, double x, double y)
    {
        // Small bookmark-like tab centered on the marker line.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x - 7, y - 12), true);
            g.LineTo(new Point(x + 7, y - 12));
            g.LineTo(new Point(x + 7, y - 1));
            g.LineTo(new Point(x, y + 4));
            g.LineTo(new Point(x - 7, y - 1));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        ctx.DrawText(ft, at);
    }

    private static string FormatMinutes(double minutes)
    {
        if (minutes <= 0) return "0 min";
        // Whole minutes read as "10 mins"; fractional as mm:ss.
        if (Math.Abs(minutes - Math.Round(minutes)) < 0.001)
            return $"{(int)Math.Round(minutes)} mins";
        var totalSec = (int)Math.Round(minutes * 60);
        return $"{totalSec / 60}:{totalSec % 60:00} min";
    }

    // -------- Marker dragging --------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double xStart = XAt(RampStartFraction), xEnd = XAt(RampEndFraction);
        // Grab whichever handle is nearer (within 14px); ties toward the closer one.
        var dStart = Math.Abs(p.X - xStart);
        var dEnd = Math.Abs(p.X - xEnd);
        if (Math.Min(dStart, dEnd) > 16) return;
        _drag = dStart <= dEnd ? DragTarget.Start : DragTarget.End;
        e.Pointer.Capture(this);
        UpdateFromPointer(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag != DragTarget.None) UpdateFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = DragTarget.None;
        e.Pointer.Capture(null);
    }

    private void UpdateFromPointer(Point p)
    {
        var plot = Plot;
        var frac = Math.Clamp((p.X - plot.X) / plot.Width, 0, 1);
        if (_drag == DragTarget.Start)
        {
            var end = RampEndFraction;
            // Keep an initial-hold phase and a ramp segment; never let the ramp span exceed MaxRampSpan.
            var start = Math.Clamp(frac, 0, end - MinPhase);
            if (end - start > MaxRampSpan) start = end - MaxRampSpan;
            RampStartFraction = Math.Round(start, 3);
        }
        else if (_drag == DragTarget.End)
        {
            var start = RampStartFraction;
            var end = Math.Clamp(frac, start + MinPhase, 1);
            if (end - start > MaxRampSpan) end = start + MaxRampSpan;
            RampEndFraction = Math.Round(end, 3);
        }
    }
}
