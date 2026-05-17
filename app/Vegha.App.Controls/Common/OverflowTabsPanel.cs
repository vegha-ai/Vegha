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
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            total += child.DesiredSize.Width;
            if (child.DesiredSize.Height > maxH) maxH = child.DesiredSize.Height;
        }
        NaturalWidth = total;
        // DesiredSize reports the *visible* width so the parent layout can size
        // its own column to the actual rendered tab strip and place the chevron
        // immediately after.
        if (double.IsInfinity(availableSize.Width)) return new Size(total, maxH);
        double visible = 0;
        foreach (var child in Children)
        {
            if (visible + child.DesiredSize.Width <= availableSize.Width)
                visible += child.DesiredSize.Width;
            else
                break;
        }
        return new Size(visible, maxH);
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
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            widths[i] = Children[i].DesiredSize.Width;
            total += widths[i];
        }

        // The chevron lives in a sibling Auto-width Grid column outside this
        // panel, so when it's visible our finalSize is already shrunk to
        // accommodate it — no additional internal reserve is needed. Packing
        // simply uses the available width directly.
        double max = finalSize.Width;

        // Greedy left-to-right packing within the reduced width.
        var visible = new bool[count];
        double used = 0;
        for (int i = 0; i < count; i++)
        {
            if (used + widths[i] <= max)
            {
                visible[i] = true;
                used += widths[i];
            }
        }

        // Promote the selected TabItem into the visible range — if greedy
        // packing kicked it into the hidden pool, evict the rightmost visible
        // items until there's room. Keeps the active tab in view at all widths.
        int selectedIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (Children[i] is TabItem ti && ti.IsSelected)
            {
                selectedIndex = i;
                break;
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
}
