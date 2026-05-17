using Avalonia.Controls;
using Avalonia.Media;

namespace Vegha.App.Controls.Services;

/// <summary>Application-wide UI zoom. Holds the current zoom factor and applies it to any
/// <see cref="Window"/> that opts in via <see cref="Attach"/>. Centralized here (rather than
/// per-window code-behind) for two reasons:
/// <list type="bullet">
///   <item>Transforms in XAML don't inherit DataContext from their parent Control, so binding
///         <c>ScaleX="{Binding ...}"</c> on a <see cref="ScaleTransform"/> silently no-ops.</item>
///   <item>Each child window opens with its own visual tree; the LayoutTransform on
///         MainWindow's Content doesn't propagate to dialogs. We need a per-window wrap.</item>
/// </list>
/// </summary>
public static class ZoomHost
{
    private static double s_currentZoom = 1.0;

    public static event Action<double>? ZoomChanged;

    public static double CurrentZoom => s_currentZoom;

    /// <summary>Update the zoom factor. Triggers re-scaling on every window currently
    /// attached. Called by MainWindow after loading settings and after each save.</summary>
    public static void SetZoom(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, 0.8, 2.0), 2);
        if (Math.Abs(clamped - s_currentZoom) < 0.001) return;
        s_currentZoom = clamped;
        ZoomChanged?.Invoke(clamped);
    }

    /// <summary>Wrap <paramref name="window"/>'s current <c>Content</c> in a
    /// <see cref="LayoutTransformControl"/> and bind its <see cref="ScaleTransform"/> to the
    /// shared zoom value. Call from each window's <c>Opened</c> handler. Safe to call once
    /// per window — repeated calls leave the existing host in place.</summary>
    public static void Attach(Window window)
    {
        if (window.Content is LayoutTransformControl) return;

        var inner = window.Content as global::Avalonia.Controls.Control;
        if (inner is null) return;

        window.Content = null; // detach before re-parenting

        var scale = new ScaleTransform(s_currentZoom, s_currentZoom);
        var host = new LayoutTransformControl
        {
            Child = inner,
            LayoutTransform = scale,
        };

        window.Content = host;

        // Sync future changes — and detach when the window closes so we don't leak.
        void OnZoom(double v) => scale.ScaleX = scale.ScaleY = v;
        ZoomChanged += OnZoom;
        window.Closed += (_, _) => ZoomChanged -= OnZoom;
    }
}
