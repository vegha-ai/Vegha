using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace Vegha.App.Controls.Common;

/// <summary>
/// Header-strip layout used inside the <see cref="OverflowTabControl"/> template.
/// Owns three positional children (in this exact order):
/// <list type="number">
///   <item>The <see cref="ItemsPresenter"/> that hosts the TabItem strip.</item>
///   <item>The "»" overflow chevron button.</item>
///   <item>The <see cref="ContentPresenter"/> for the trailing-tools <see cref="TabControl.Tag"/>.</item>
/// </list>
/// The chevron is parked immediately after the items presenter's actual rendered
/// width — not at the right edge of some <c>*</c> grid column — so there's no
/// dead space between the last visible tab and the chevron. The trailing tools
/// are right-aligned at the far edge so they keep their familiar position.
/// </summary>
public sealed class OverflowHeaderPanel : Panel
{
    /// <summary>Visual left offset for the tab strip. Owned by this panel (applied in
    /// Arrange) rather than via a Margin on the ItemsPresenter so the inner items panel
    /// gets a measure-slot that matches what the OverflowHeaderPanel itself computed —
    /// previously the ItemsPresenter's <c>Margin="10,0,0,0"</c> silently subtracted 10px
    /// from the inner panel's available width while the overflow check (NaturalWidth vs.
    /// slotForTabs) didn't, so the inner panel would hide a tab during arrange even when
    /// measure thought there was no overflow → chevron stuck on with dead space.</summary>
    private const double ItemsLeftPad = 10;

    /// <summary>Stable chevron-reserve used in measure regardless of the
    /// chevron's current IsVisible state. Using <c>chev.DesiredSize.Width</c>
    /// here is unstable: the first frame (HasOverflow=false → chev collapsed
    /// → DesiredSize=0) and the second frame (HasOverflow flipped true →
    /// DesiredSize=natural) would yield different slots and the items panel
    /// would settle one tab short of what fits with a real chevron. A small
    /// over-reserve is better than that oscillation.</summary>
    private const double ChevronReserve = 32;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 3)
        {
            double dw = 0, dh = 0;
            foreach (var c in Children)
            {
                c.Measure(availableSize);
                dw += c.DesiredSize.Width;
                if (c.DesiredSize.Height > dh) dh = c.DesiredSize.Height;
            }
            return new Size(dw, dh);
        }

        var items = Children[0];
        var chev = Children[1];
        var tag = Children[2];

        tag.Measure(availableSize);
        chev.Measure(availableSize);

        double tagW = tag.DesiredSize.Width;
        double chevronW = Math.Max(ChevronReserve, chev.DesiredSize.Width);
        // The trailing tools are always docked to the right edge — never packed beside
        // the items strip. So the slot available for the tab strip is the width minus
        // the tag and the items' left padding. The chevron lives in the dead space
        // between the last visible tab and the right-aligned tag when overflow occurs.
        double slotForTabs = Math.Max(0, availableSize.Width - tagW - ItemsLeftPad);

        items.Measure(new Size(slotForTabs, availableSize.Height));
        var inner = items.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
        double naturalTabsWidth = inner?.NaturalWidth ?? items.DesiredSize.Width;
        bool overflow = naturalTabsWidth > slotForTabs + 0.5;
        if (overflow)
        {
            items.Measure(new Size(Math.Max(0, slotForTabs - chevronW), availableSize.Height));
        }

        double height = Math.Max(items.DesiredSize.Height,
                          Math.Max(chev.DesiredSize.Height, tag.DesiredSize.Height));
        return new Size(double.IsInfinity(availableSize.Width)
            ? ItemsLeftPad + items.DesiredSize.Width + (overflow ? chevronW : 0) + tagW
            : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 3)
        {
            double x = 0;
            foreach (var c in Children)
            {
                c.Arrange(new Rect(x, 0, c.DesiredSize.Width, finalSize.Height));
                x += c.DesiredSize.Width;
            }
            return finalSize;
        }

        var items = Children[0];
        var chev = Children[1];
        var tag = Children[2];

        double tagW = tag.DesiredSize.Width;
        double itemsW = items.DesiredSize.Width;
        double chW = chev.IsVisible ? chev.DesiredSize.Width : 0;

        items.Arrange(new Rect(ItemsLeftPad, 0, itemsW, finalSize.Height));
        // Chevron sits flush against the right edge of the items presenter's
        // rendered width (= last visible tab) rather than at the right edge of
        // a wider grid column. When the chevron is collapsed (IsVisible=false)
        // we arrange it at zero size so it doesn't reserve any space.
        if (chev.IsVisible)
            chev.Arrange(new Rect(ItemsLeftPad + itemsW, 0, chW, finalSize.Height));
        else
            chev.Arrange(new Rect(0, 0, 0, 0));
        // Trailing tools are always right-aligned to the panel's far edge — both when
        // all tabs fit (no chevron) and when the chevron is shown. The Math.Max guard
        // pushes the tag right only if the items + chevron would otherwise overlap a
        // strictly right-aligned tag, which only happens on extreme widths where the
        // tab strip is already eating into the tag's reserved area.
        double tagX = Math.Max(ItemsLeftPad + itemsW + chW, finalSize.Width - tagW);
        tag.Arrange(new Rect(tagX, 0, tagW, finalSize.Height));

        return finalSize;
    }
}
