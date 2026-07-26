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
    /// <summary>Slack allowed when testing whether a tab fits. Layout rounding snaps each
    /// child's desired width and the panel's own arrange rect to whole device pixels
    /// independently, so at fractional render scales (125% / 150%) the sum of the parts can
    /// land a hair above the whole. Without this tolerance the last tab was evicted — and
    /// the chevron lit up — on a strip that visibly had hundreds of pixels to spare.</summary>
    private const double Epsilon = 1.0;

    /// <summary>Sum of the visible children's natural widths. The parent
    /// <see cref="OverflowHeaderPanel"/> uses this to decide whether the overflow chevron
    /// is needed — comparing natural total against the slot available for tabs.</summary>
    public double NaturalWidth { get; private set; }

    /// <summary>Width actually consumed by the tabs that survived the last arrange pass.
    /// <see cref="OverflowHeaderPanel"/> parks the chevron here so it always sits flush
    /// against the real right edge of the last visible tab, never against a stale
    /// desired-width that assumed a different set of tabs.</summary>
    public double VisibleWidth { get; private set; }

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
            if (!child.IsVisible) continue;
            total += child.DesiredSize.Width;
            if (child.DesiredSize.Height > maxH) maxH = child.DesiredSize.Height;
        }
        NaturalWidth = total;
        if (double.IsInfinity(availableSize.Width)) return new Size(total, maxH);
        // DesiredSize reports the *fitting* width, using the same packing rule arrange
        // uses — the two passes must agree or arrange silently hides a tab that measure
        // budgeted room for.
        return new Size(Pack(availableSize.Width, null), maxH);
    }

    /// <summary>Greedy left-to-right fit within <paramref name="max"/>, stopping at the
    /// first tab that doesn't fit so overflow is always a suffix of the strip (a tab never
    /// jumps the queue just because it's narrow). Collapsed children — e.g. the SOAP tab on
    /// a non-SOAP request — take no width and are never counted as overflow. Fills
    /// <paramref name="visible"/> when supplied; returns the width consumed.</summary>
    private double Pack(double max, bool[]? visible)
    {
        double used = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible)
            {
                if (visible is not null) visible[i] = true;
                continue;
            }
            var w = child.DesiredSize.Width;
            if (used + w > max + Epsilon) break;
            used += w;
            if (visible is not null) visible[i] = true;
        }
        return used;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0)
        {
            VisibleWidth = 0;
            this.FindAncestorOfType<OverflowTabControl>()?.ReportOverflowState(Array.Empty<Control>());
            return finalSize;
        }

        // The chevron lives outside this panel, so when it's shown our finalSize has
        // already been shrunk to accommodate it — no additional internal reserve needed.
        var visible = new bool[count];
        double used = Pack(finalSize.Width, visible);

        // Promote the selected TabItem into the visible range — if packing kicked it into
        // the hidden pool, evict the rightmost visible items until there's room. Keeps the
        // active tab in view at all widths.
        int selectedIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (Children[i] is TabItem { IsSelected: true })
            {
                selectedIndex = i;
                break;
            }
        }
        if (selectedIndex >= 0 && !visible[selectedIndex])
        {
            var need = Children[selectedIndex].DesiredSize.Width;
            for (int i = count - 1; i >= 0 && used + need > finalSize.Width + Epsilon; i--)
            {
                if (visible[i] && i != selectedIndex && Children[i].IsVisible)
                {
                    visible[i] = false;
                    used -= Children[i].DesiredSize.Width;
                }
            }
            visible[selectedIndex] = true;
            used += need;
        }

        double x = 0;
        var hidden = new List<Control>();
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            var w = child.DesiredSize.Width;
            if (visible[i])
            {
                child.Arrange(new Rect(x, 0, w, finalSize.Height));
                if (child.IsVisible) x += w;
            }
            else
            {
                // Off-screen; ClipToBounds hides them and they're not hit-testable.
                child.Arrange(new Rect(finalSize.Width + 1, 0, w, finalSize.Height));
                hidden.Add(child);
            }
        }

        VisibleWidth = x;
        this.FindAncestorOfType<OverflowTabControl>()?.ReportOverflowState(hidden);
        return finalSize;
    }
}
