using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Vegha.App.Controls.Shell;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Guards the "+ (new request)" affordance in the request tab strip. The inline "+" lives
/// inside the scrolling tab run so it trails the last tab browser-style — which meant that
/// once enough tabs were open it scrolled off the viewport along with them and there was no
/// way to open a new request from the strip. A pinned twin at the far right now takes over
/// whenever the strip overflows; exactly one of the two is ever visible.
/// </summary>
public class RequestTabStripNewTabVisibilityTests
{
    private static OpenTabsViewModel NewTabs()
    {
        Func<RequestEditorViewModel> factory = () => new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
        return new OpenTabsViewModel(factory, NullLogger<OpenTabsViewModel>.Instance);
    }

    private static OpenTabsViewModel WithTabs(int count)
    {
        var tabs = NewTabs();
        for (int i = 0; i < count; i++)
        {
            tabs.OpenOrActivate(
                new RequestItem
                {
                    Name = $"Request number {i}",
                    Method = "GET",
                    Url = $"https://example.com/{i}",
                    Kind = RequestKind.Http,
                },
                $"/A/r{i}.bru", collectionPath: "/A");
        }
        tabs.ActiveScope = "/A";
        return tabs;
    }

    private static Button Find(RequestTabStrip strip, string name) =>
        strip.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    /// <summary>Hosts the strip in a fixed-width Border — the headless window doesn't always
    /// propagate Window.Width changes, and we need a width we control precisely.</summary>
    private static (RequestTabStrip Strip, Window Win, Border Host) Host(OpenTabsViewModel tabs, double width)
    {
        var strip = new RequestTabStrip { DataContext = tabs };
        var host = new Border { Width = width, Height = 40, Child = strip };
        var win = new Window { Width = 2000, Height = 200, Content = host };
        win.Show();
        win.UpdateLayout();
        win.UpdateLayout();
        win.UpdateLayout();
        return (strip, win, host);
    }

    /// <summary>Whatever the tab count and whatever the width, a "+" must be on screen.
    /// This is the user-visible contract; which of the two buttons provides it is detail.</summary>
    [AvaloniaTheory]
    [InlineData(1, 1200)]
    [InlineData(4, 1200)]
    [InlineData(12, 1200)]
    [InlineData(30, 1200)]
    [InlineData(30, 400)]
    [InlineData(1, 200)]
    public void NewTabButton_IsAlwaysReachable(int tabCount, double width)
    {
        var tabs = WithTabs(tabCount);
        var (strip, win, _) = Host(tabs, width);
        try
        {
            var inline = Find(strip, "NewTabButton");
            var pinned = Find(strip, "PinnedNewTabButton");

            (inline.IsVisible || pinned.IsVisible).Should().BeTrue(
                $"a \"+\" must be reachable with {tabCount} tabs at {width}px");
            (inline.IsVisible && pinned.IsVisible).Should().BeFalse(
                "showing two \"+\" buttons at once would read as a duplicate control");

            // The visible one must actually be inside the strip's viewport, not scrolled off.
            var shown = inline.IsVisible ? inline : pinned;
            var topLeft = shown.TranslatePoint(default, strip)!.Value;
            topLeft.X.Should().BeGreaterThanOrEqualTo(-0.5);
            (topLeft.X + shown.Bounds.Width).Should().BeLessThanOrEqualTo(width + 0.5,
                $"the visible \"+\" must sit within the {width}px strip, not off its right edge");
        }
        finally { win.Close(); }
    }

    /// <summary>With room to spare the "+" stays inline, right after the last tab — the
    /// browser-style placement that keeps it a short mouse travel from the tab the user
    /// just clicked. The pinned twin must stay out of the way.</summary>
    [AvaloniaFact]
    public void FewTabs_InlinePlusFollowsTheLastTab()
    {
        var tabs = WithTabs(2);
        var (strip, win, _) = Host(tabs, 1400);
        try
        {
            var inline = Find(strip, "NewTabButton");
            var pinned = Find(strip, "PinnedNewTabButton");
            inline.IsVisible.Should().BeTrue("2 tabs leave plenty of room for the inline \"+\"");
            pinned.IsVisible.Should().BeFalse();

            var lastTab = strip.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("reqTab"))
                .OrderBy(b => b.TranslatePoint(default, strip)!.Value.X)
                .Last();
            var lastRight = lastTab.TranslatePoint(default, strip)!.Value.X + lastTab.Bounds.Width;
            var plusLeft = inline.TranslatePoint(default, strip)!.Value.X;
            plusLeft.Should().BeApproximately(lastRight, 1.0,
                "the inline \"+\" should sit flush against the last tab");
        }
        finally { win.Close(); }
    }

    /// <summary>The overflow decision must not depend on chrome that the decision itself
    /// toggles, or the strip flip-flops between states across layout passes. Repeated
    /// layout passes must therefore land on the same answer.</summary>
    [AvaloniaFact]
    public void OverflowState_IsStableAcrossLayoutPasses()
    {
        var tabs = WithTabs(20);
        var (strip, win, host) = Host(tabs, 900);
        try
        {
            var inline = Find(strip, "NewTabButton");
            var pinned = Find(strip, "PinnedNewTabButton");

            // Sweep widths across the fit/overflow boundary; at each stop the answer must
            // settle rather than alternate between successive layout passes.
            for (double w = 400; w <= 1900; w += 50)
            {
                host.Width = w;
                win.UpdateLayout();
                win.UpdateLayout();
                var first = (inline.IsVisible, pinned.IsVisible);
                win.UpdateLayout();
                win.UpdateLayout();
                (inline.IsVisible, pinned.IsVisible).Should().Be(first,
                    $"the \"+\" placement must settle at width {w}, not oscillate");
                (inline.IsVisible ^ pinned.IsVisible).Should().BeTrue(
                    $"exactly one \"+\" must be visible at width {w}");
            }
        }
        finally { win.Close(); }
    }

    /// <summary>Scroll arrows and the pinned "+" appear together: both are overflow-only
    /// chrome, and an arrow-less strip that still hides the inline "+" would strand the
    /// user with no way to add a tab.</summary>
    [AvaloniaFact]
    public void ScrollArrows_TrackTheSameOverflowStateAsThePinnedPlus()
    {
        var tabs = WithTabs(25);
        var (strip, win, host) = Host(tabs, 500);
        try
        {
            var pinned = Find(strip, "PinnedNewTabButton");
            var left = Find(strip, "ScrollLeftButton");
            var right = Find(strip, "ScrollRightButton");

            pinned.IsVisible.Should().BeTrue("25 tabs cannot fit in 500px");
            left.IsVisible.Should().BeTrue();
            right.IsVisible.Should().BeTrue();
            right.IsEnabled.Should().BeTrue("there is content to the right at offset 0");

            host.Width = 1900;
            tabs.CloseAll();
            win.UpdateLayout();
            win.UpdateLayout();
            pinned.IsVisible.Should().BeFalse("no tabs, no overflow");
            left.IsVisible.Should().BeFalse();
            right.IsVisible.Should().BeFalse();
        }
        finally { win.Close(); }
    }
}
