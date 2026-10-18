using System;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Common;

/// <summary>
/// Two-child horizontal layout: a shrinkable leading child (typically a ScrollViewer) followed
/// by a fixed trailing child that always stays on screen. The trailing child is measured first
/// and reserved, the leading child gets whatever is left, and the trailing child is parked flush
/// against the leading child's actual width — so it reads as "inline after the last item" while
/// everything fits, and pins to the right edge once the leading child has to scroll.
/// Used by the request tab strip so the "+" new-tab button never scrolls out of view.
/// </summary>
public sealed class InlineTrailingPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            if (Children.Count == 1) { Children[0].Measure(availableSize); return Children[0].DesiredSize; }
            return default;
        }

        var lead = Children[0];
        var trail = Children[1];
        trail.Measure(availableSize);
        double trailW = trail.DesiredSize.Width;
        lead.Measure(new Size(Math.Max(0, availableSize.Width - trailW), availableSize.Height));

        return new Size(lead.DesiredSize.Width + trailW,
            Math.Max(lead.DesiredSize.Height, trail.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            if (Children.Count == 1) Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }

        var lead = Children[0];
        var trail = Children[1];
        double trailW = trail.DesiredSize.Width;
        double leadW = Math.Min(lead.DesiredSize.Width, Math.Max(0, finalSize.Width - trailW));
        lead.Arrange(new Rect(0, 0, leadW, finalSize.Height));
        trail.Arrange(new Rect(leadW, 0, trailW, finalSize.Height));
        return finalSize;
    }
}
