using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Vegha.App.Controls.Common;

/// <summary>
/// TabControl variant that hides tabs which don't fit horizontally and exposes
/// them through a "»" dropdown chevron docked to the right of the header strip.
/// Drop-in replacement for the previous <c>TabControl Classes="subtabs"</c>
/// scrollviewer-based overflow, which was easy to miss on narrow widths. The
/// <see cref="TabControl.Tag"/> slot is preserved so consumers can keep docking
/// trailing tools (e.g. the body-type picker + Prettify buttons) to the right
/// of the header strip.
/// </summary>
public class OverflowTabControl : TabControl
{
    public static readonly DirectProperty<OverflowTabControl, bool> HasOverflowProperty =
        AvaloniaProperty.RegisterDirect<OverflowTabControl, bool>(
            nameof(HasOverflow), o => o.HasOverflow);

    private bool _hasOverflow;
    public bool HasOverflow
    {
        get => _hasOverflow;
        private set => SetAndRaise(HasOverflowProperty, ref _hasOverflow, value);
    }

    public static readonly DirectProperty<OverflowTabControl, IReadOnlyList<Control>> HiddenItemsProperty =
        AvaloniaProperty.RegisterDirect<OverflowTabControl, IReadOnlyList<Control>>(
            nameof(HiddenItems), o => o.HiddenItems);

    private IReadOnlyList<Control> _hiddenItems = Array.Empty<Control>();
    public IReadOnlyList<Control> HiddenItems
    {
        get => _hiddenItems;
        private set => SetAndRaise(HiddenItemsProperty, ref _hiddenItems, value);
    }

    private Button? _overflowButton;
    private MenuFlyout? _overflowFlyout;

    protected override Type StyleKeyOverride => typeof(OverflowTabControl);

    public OverflowTabControl()
    {
        // Apply a marker class so the TabItem styles in Styles.axaml can match
        // via `TabControl.overflowtabs TabItem /template/…` (a class-based
        // selector is friendlier than namespace-prefixed type selectors when
        // it comes to `/template/` reach-through into the Fluent TabItem
        // template — the cmn|OverflowTabControl form was silently dropping
        // the SelectedPipe-hide rule on our setup).
        Classes.Add("overflowtabs");

        // The panel's pack ensures the selected tab stays visible — but it only
        // re-runs on arrange. Force a re-arrange whenever selection changes so a
        // programmatic selection (e.g. user picking a tab from the overflow menu)
        // moves the newly-active tab back into the visible strip immediately.
        SelectionChanged += (_, _) =>
        {
            this.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault()?.InvalidateArrange();
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _overflowButton = e.NameScope.Find<Button>("PART_OverflowButton");
        _overflowFlyout = _overflowButton?.Flyout as MenuFlyout;
        RebuildFlyoutItems();
    }

    internal void ReportOverflowState(IReadOnlyList<Control> hidden)
    {
        HiddenItems = hidden;
        HasOverflow = hidden.Count > 0;
        // Rebuild eagerly so the flyout already has its items when the user
        // clicks the chevron. Populating during Opening was unreliable — the
        // popup measured with an empty Items list and rendered as a thin
        // gray bar.
        RebuildFlyoutItems();
    }

    private void RebuildFlyoutItems()
    {
        if (_overflowFlyout == null) return;
        _overflowFlyout.Items.Clear();
        foreach (var item in HiddenItems)
        {
            var captured = item;
            var mi = new MenuItem { Header = ExtractHeaderText(item) };
            mi.Click += (_, _) => SelectedItem = captured;
            _overflowFlyout.Items.Add(mi);
        }
    }

    /// <summary>Pulls a plain-text title from a TabItem header. Handles the
    /// common shapes used in the request workspace: <c>Header="Settings"</c>
    /// (string), or <c>&lt;StackPanel&gt;&lt;TextBlock Text="Body"/&gt;…
    /// &lt;/StackPanel&gt;</c> where the first non-empty TextBlock is the
    /// label and the rest are count/dot badges. Composite headers with badges
    /// (e.g. <c>Params (3)</c>) deliberately collapse to just the label in
    /// the menu — the menu is for navigation, not at-a-glance counts.</summary>
    private static string ExtractHeaderText(Control item)
    {
        if (item is HeaderedContentControl hc) return ExtractHeaderText(hc.Header);
        return item.ToString() ?? "";
    }

    private static string ExtractHeaderText(object? header) => header switch
    {
        null => "",
        string s => s,
        TextBlock tb => tb.Text ?? "",
        Control c => c.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "",
        _ => header.ToString() ?? ""
    };
}
