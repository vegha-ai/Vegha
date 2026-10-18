using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Vegha.App.Controls.Common;

/// <summary>
/// Horizontal items panel used by <see cref="OverflowTabControl"/>. Lays children
/// left-to-right; any TabItem that doesn't fit the available width is arranged
/// off the right edge (clipped) and reported to the parent control via
/// <see cref="OverflowTabControl.ReportOverflowState"/>, which surfaces them
/// through a "»" dropdown chevron. The currently selected tab is always kept in
/// the visible range so the user never loses sight of what's active.
/// </summary>
public sealed class OverflowTabsPanel : Panel
{
    /// <summary>Sum of all children's natural widths (regardless of visibility).
    /// The parent <see cref="OverflowHeaderPanel"/> uses this to decide whether
    /// the overflow chevron is needed — comparing natural total against the
    /// slot available for tabs.</summary>
    public double NaturalWidth { get; private set; }

    /// <summary>Visible/hidden decision made in measure. Arrange places children from this
    /// set instead of re-packing: at fractional DPI, layout rounding can hand arrange a
    /// width a fraction of a pixel narrower than measure saw, and re-packing against it
    /// evicted the last tab while the presenter still reserved its slot — the chevron
    /// then appeared after a tab-wide dead gap.</summary>
    private bool[] _visible = Array.Empty<bool>();

    public OverflowTabsPanel()
    {
        // Overflow tabs are arranged just past the right edge; clipping keeps
        // them from bleeding into the chevron / trailing-tools slot.
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double total = 0;
        double maxH = 0;
        var count = Children.Count;
        var widths = new double[count];
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = child.DesiredSize.Width;
            total += widths[i];
            if (child.DesiredSize.Height > maxH) maxH = child.DesiredSize.Height;
        }
        NaturalWidth = total;

        _visible = Pack(widths, availableSize.Width, SelectedIndex());
        // DesiredSize reports the *visible* width so the parent layout can size
        // its own column to the actual rendered tab strip and place the chevron
        // immediately after.
        double visibleW = 0;
        for (int i = 0; i < count; i++)
            if (_visible[i]) visibleW += widths[i];
        return new Size(visibleW, maxH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0)
        {
            this.FindAncestorOfType<OverflowTabControl>()?.ReportOverflowState(Array.Empty<Control>());
            return finalSize;
        }

        var widths = new double[count];
        for (int i = 0; i < count; i++)
            widths[i] = Children[i].DesiredSize.Width;

        // Children changed without a measure pass — fall back to packing here.
        var visible = _visible.Length == count ? _visible : Pack(widths, finalSize.Width, SelectedIndex());

        double x = 0;
        var hidden = new List<Control>();
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            if (visible[i])
            {
                child.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
                x += widths[i];
            }
            else
            {
                // Off-screen; ClipToBounds hides them and they're not hit-testable.
                child.Arrange(new Rect(finalSize.Width + 1, 0, widths[i], finalSize.Height));
                hidden.Add(child);
            }
        }

        this.FindAncestorOfType<OverflowTabControl>()?.ReportOverflowState(hidden);
        return finalSize;
    }

    private int SelectedIndex()
    {
        for (int i = 0; i < Children.Count; i++)
            if (Children[i] is TabItem { IsSelected: true }) return i;
        return -1;
    }

    /// <summary>Greedy left-to-right packing into <paramref name="max"/>, then promotes the
    /// selected tab into the visible range — if packing kicked it into the hidden pool,
    /// evict the rightmost visible items until there's room. Keeps the active tab in view
    /// at all widths.</summary>
    private static bool[] Pack(double[] widths, double max, int selectedIndex)
    {
        var count = widths.Length;
        var visible = new bool[count];
        if (double.IsInfinity(max))
        {
            Array.Fill(visible, true);
            return visible;
        }

        double used = 0;
        for (int i = 0; i < count; i++)
        {
            if (used + widths[i] <= max)
            {
                visible[i] = true;
                used += widths[i];
            }
        }

        if (selectedIndex >= 0 && !visible[selectedIndex])
        {
            for (int i = count - 1; i >= 0 && used + widths[selectedIndex] > max; i--)
            {
                if (visible[i] && i != selectedIndex)
                {
                    visible[i] = false;
                    used -= widths[i];
                }
            }
            visible[selectedIndex] = true;
        }
        return visible;
    }
}
