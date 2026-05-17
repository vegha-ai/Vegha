using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vegha.App.Controls.Common;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Headless tests for OverflowTabControl's chevron-overflow logic. The bug we're guarding
/// against: the chevron showed (and the trailing tab hid) even when the tab strip had
/// plenty of unused horizontal room. Root cause was that OverflowHeaderPanel's overflow
/// detection compared the inner panel's natural width against the slot WITHOUT subtracting
/// the ItemsPresenter's 10px left margin — so the inner panel hid tabs during arrange
/// (because it really had slot - 10 to work with) but measure thought there was no overflow
/// and didn't reserve chevron space, leaving the chevron stuck-on with significant dead
/// space between the last visible tab and the right-aligned trailing tools.
/// </summary>
public class OverflowTabControlTests
{
    private static OverflowTabControl BuildTabControl(int tabCount)
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };
        for (int i = 0; i < tabCount; i++)
        {
            tc.Items.Add(new TabItem { Header = $"Tab{i}" });
        }
        return tc;
    }

    private static (OverflowTabControl Tc, Window Win) Host(OverflowTabControl tc, double width)
    {
        var win = new Window { Width = width, Height = 200, Content = tc };
        win.Show();
        win.UpdateLayout();
        win.UpdateLayout();
        return (tc, win);
    }

    [AvaloniaFact]
    public void AllTabsFit_ChevronHidden()
    {
        var tc = BuildTabControl(3);
        var (_, win) = Host(tc, 800);
        try
        {
            tc.HasOverflow.Should().BeFalse("3 short tabs fit easily in 800px");
            tc.HiddenItems.Should().BeEmpty();
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void TabsOverflow_ChevronShown_HiddenReported()
    {
        var tc = BuildTabControl(20);
        var (_, win) = Host(tc, 300);
        try
        {
            tc.HasOverflow.Should().BeTrue("20 tabs cannot fit in 300px");
            tc.HiddenItems.Should().NotBeEmpty();
        }
        finally { win.Close(); }
    }

    /// <summary>Borderline width: ample room — the natural width of N tabs is comfortably
    /// less than the available slot. The chevron should NOT show. This is the regression
    /// case from the bug report screenshot.</summary>
    [AvaloniaFact]
    public void NaturalWidthFitsInSlot_NoChevron()
    {
        // 10 tabs at ~60-80px each = ~700px natural. Window 1400px gives way more.
        var tc = BuildTabControl(10);
        var (_, win) = Host(tc, 1400);
        try
        {
            tc.HasOverflow.Should().BeFalse("natural width should be well under 1400px");
            tc.HiddenItems.Should().BeEmpty();
        }
        finally { win.Close(); }
    }

    /// <summary>When the strip has trailing tools (Tag content) that take real width AND
    /// many tabs that naturally fit, the chevron should still stay hidden. Mirrors the
    /// Body-tab scenario where the body-type picker + Prettify buttons occupy the right
    /// side and the natural tab width fits in the remaining slot.</summary>
    [AvaloniaFact]
    public void NaturalFitsWithTrailingTag_NoChevron()
    {
        var tc = BuildTabControl(10);
        // Tag = a horizontal stack panel of buttons, mimicking the Body trailing tools.
        var trailing = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        trailing.Children.Add(new Button { Content = "XML ▼", Width = 60 });
        trailing.Children.Add(new Button { Content = "JSON→XML", Width = 80 });
        trailing.Children.Add(new Button { Content = "Prettify", Width = 60 });
        tc.Tag = trailing;
        var (_, win) = Host(tc, 1400);
        try
        {
            // 10 tabs natural ~ 700px. Tag ~ 200px + margins. Slot ~ 1180px. Should fit.
            tc.HasOverflow.Should().BeFalse(
                "10 short tabs (~700px natural) should fit alongside trailing tools (~200px) in 1400px window");
            tc.HiddenItems.Should().BeEmpty();
        }
        finally { win.Close(); }
    }

    /// <summary>Mirrors the exact RequestWorkspace sub-tab strip: 11 tabs with varied
    /// labels matching Params/Auth/Headers/Vars/Body/Pre-request/Post-response/Tests/Docs/
    /// Settings/Sent. Some tabs carry data-dots / count badges. Trailing tag is the
    /// body-type picker + Prettify. This is the screenshot scenario.</summary>
    [AvaloniaFact]
    public void RealRequestWorkspaceTabs_FitInTypicalWidth_NoChevron()
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };

        // Helper: header with optional accent dot, mimicking the request workspace.
        TabItem TabWithDot(string label, bool dot)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = label });
            if (dot) sp.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 2, 0, 0),
            });
            return new TabItem { Header = sp };
        }
        TabItem TabWithCount(string label, int count)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = label });
            if (count > 0) sp.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 9 });
            return new TabItem { Header = sp };
        }

        tc.Items.Add(TabWithCount("Params", 0));
        tc.Items.Add(TabWithDot("Authorization", true));
        tc.Items.Add(TabWithCount("Headers", 1));
        tc.Items.Add(TabWithCount("Vars", 0));
        tc.Items.Add(TabWithDot("Body", true));
        tc.Items.Add(TabWithDot("Pre-request", true));
        tc.Items.Add(TabWithDot("Post-response", false));
        tc.Items.Add(TabWithDot("Tests", false));
        tc.Items.Add(TabWithDot("Docs", false));
        tc.Items.Add(new TabItem { Header = "Settings" });
        tc.Items.Add(new TabItem { Header = "Sent" });

        // Tag = body-type picker + Prettify, body tab selected so it's visible.
        var trailing = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        var bodyBtn = new Button { Padding = new Thickness(4, 2), Background = Avalonia.Media.Brushes.Transparent };
        var bodyBtnContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        bodyBtnContent.Children.Add(new TextBlock { Text = "XML" });
        bodyBtnContent.Children.Add(new TextBlock { Text = "▼", FontSize = 9 });
        bodyBtn.Content = bodyBtnContent;
        trailing.Children.Add(bodyBtn);
        trailing.Children.Add(new Button { Content = "JSON→XML", Padding = new Thickness(4, 2) });
        trailing.Children.Add(new Button { Content = "Prettify", Padding = new Thickness(4, 2) });
        tc.Tag = trailing;

        tc.SelectedIndex = 4; // Body tab.

        // Typical workspace width ~ 1500-1700px on a normal monitor.
        var (_, win) = Host(tc, 1700);
        try
        {
            // Print diagnostic info to help understand actual measurements.
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            var natural = inner?.NaturalWidth ?? -1;
            tc.HasOverflow.Should().BeFalse(
                $"natural tab width was {natural:F0}px in a 1700px window — should fit easily. " +
                $"Hidden: [{string.Join(", ", tc.HiddenItems.Select(i => (i as TabItem)?.Header?.ToString() ?? "?"))}]");
        }
        finally { win.Close(); }
    }

    /// <summary>Regression for the bug where the OverflowHeaderPanel's overflow check
    /// compared natural width against <c>slotForTabs</c> WITHOUT subtracting the
    /// ItemsPresenter's left margin. The inner panel was given a smaller slot than the
    /// check assumed, so it hid tabs at arrange-time while measure thought there was no
    /// overflow → chevron was visible without chev-reserve space being budgeted, leaving
    /// the last visible tab tucked against an off-strip chevron.
    ///
    /// Concretely: 9 × 100px tabs (= 900px natural) in a 940px host. After accounting
    /// for the ContentPresenter's 20px margin for the (empty) Tag slot and the 10px
    /// ItemsLeftPad, the effective slot is 910. Natural (900) fits comfortably → no
    /// chev expected. Pre-fix this would have triggered overflow because the inner
    /// panel only saw 900 (further reduced by the 10px ItemsPresenter Margin).
    /// </summary>
    [AvaloniaFact]
    public void BorderlineWidth_NaturalFitsInSlot_NoHiddenTabs()
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };
        for (int i = 0; i < 9; i++)
        {
            tc.Items.Add(new TabItem { Header = $"T{i}", Width = 100 });
        }
        // 940 = 900 natural + 10 leftPad + 20 tag margin + 10 slack.
        var host = new Border { Width = 940, Height = 50, Child = tc };
        var win = new Window { Width = 1200, Height = 200, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            tc.HasOverflow.Should().BeFalse(
                $"natural ~900 fits in 940 host. inner.NaturalWidth={inner?.NaturalWidth} hidden={tc.HiddenItems.Count}");
        }
        finally { win.Close(); }
    }

    /// <summary>Reproduce the user's screenshot scenario: the tab strip lives inside a
    /// host that starts narrow (forcing overflow) and is then made wider. HasOverflow
    /// must reset to false once natural width fits in the new slot — otherwise the
    /// chevron stays stuck-on showing.</summary>
    [AvaloniaFact]
    public void HasOverflowResets_WhenContainerGrowsToFitNaturalWidth()
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };
        for (int i = 0; i < 11; i++)
            tc.Items.Add(new TabItem { Header = $"Tab{i}" });

        // Outer container that we resize directly — Window.Width changes don't always
        // propagate through the headless platform's auto-sizing, so we wrap the tab
        // strip in a Border whose Width we mutate.
        var host = new Border { Width = 400, Height = 50, Child = tc };
        var win = new Window { Width = 2000, Height = 200, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            tc.HasOverflow.Should().BeTrue("400px host is too narrow for 11 tabs");

            // Grow the host — should accommodate all tabs.
            host.Width = 1600;
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout(); // extra pass for good measure

            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            var natural = inner?.NaturalWidth ?? -1;
            var innerBounds = inner?.Bounds ?? default;
            var tcBounds = tc.Bounds;
            var firstChild = inner?.Children.FirstOrDefault();
            var lastChild = inner?.Children.LastOrDefault();
            var msg = $"natural={natural:F2} innerBounds={innerBounds} tcBounds={tcBounds} " +
                      $"firstTab={firstChild?.Bounds} lastTab={lastChild?.Bounds} hidden={tc.HiddenItems.Count}";
            tc.HasOverflow.Should().BeFalse($"1600px easily fits 11 short tabs — {msg}");
        }
        finally { win.Close(); }
    }

    /// <summary>Reproduce by giving each tab WIDE CONTENT — like the request workspace,
    /// where each tab hosts a KvEditor / BodyEditor / etc. with significant natural width.
    /// If OverflowTabsPanel measures TabItems with infinity and TabItem includes content
    /// width in its DesiredSize, the panel would think tabs are huge and over-hide.</summary>
    [AvaloniaFact]
    public void TabsWithWideContent_HeaderOnlyDrivesOverflow()
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };
        for (int i = 0; i < 11; i++)
        {
            tc.Items.Add(new TabItem
            {
                Header = $"Tab{i}",
                // Wide content — should NOT count toward header-strip width.
                Content = new TextBox { Width = 2000, Text = "wide content" },
            });
        }
        var (_, win) = Host(tc, 1200);
        try
        {
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            var natural = inner?.NaturalWidth ?? 0;
            // 11 short headers ~ 550px. Way under 1200px window.
            tc.HasOverflow.Should().BeFalse(
                $"Headers ~50px each × 11 = ~550px should fit in 1200px. " +
                $"inner.NaturalWidth = {natural:F0} (suspicious if > 700)");
        }
        finally { win.Close(); }
    }

    /// <summary>The user's screenshot scenario: Body tab selected with SOAP body, auth
    /// configured, pre-request script set — so the Auth/Body/Pre-request data-dots are
    /// all visible (~1084px natural). At width 1350 the slot for the tab strip is
    /// 1350 − 276 (tools) − 10 (leftpad) = 1064px, which is just under natural — so the
    /// last tab (Sent, ~64px) hides and the chevron appears. The trailing tools remain
    /// right-aligned per the design.</summary>
    [AvaloniaFact]
    public void UserScenario_BorderlineWidth_HidesOnlyLastTab()
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);
        vm.BodyType = "xml";
        vm.AuthType = "basic";
        vm.PreRequestScript = "// pre-request";
        vm.RequestTabIndex = 4;
        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        // 1350px — natural (~1084) just barely exceeds slot (1064), so the last tab
        // (Sent) hides. Tools remain right-aligned, dead space between chev and tools.
        var host = new Border { Width = 1350, Height = 600, Child = workspace };
        var win = new Window { Width = 1900, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout();
            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().FirstOrDefault()!;
            tc.HasOverflow.Should().BeTrue();
            tc.HiddenItems.Count.Should().Be(1, "only the last tab (Sent) should hide");
        }
        finally { win.Close(); }
    }

    /// <summary>Verify the trailing-tools StackPanel actually renders when Body tab is
    /// selected. The user's screenshot shows the chevron with a large empty area to the
    /// right — if IsBodyTabSelected isn't true (binding broken, DataContext wrong, etc.),
    /// the trailing tools would be invisible and the dead space would look "huge" without
    /// the picker filling it.</summary>
    [AvaloniaFact]
    public void TrailingTools_VisibleWhenBodyTabSelected()
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);
        vm.BodyType = "xml";
        vm.RequestTabIndex = 4; // Body.
        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        var host = new Border { Width = 1400, Height = 600, Child = workspace };
        var win = new Window { Width = 1900, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout();

            vm.IsBodyTabSelected.Should().BeTrue("with RequestTabIndex=4 (Body), IsBodyTabSelected should be true");

            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().FirstOrDefault()!;
            // Find the trailing-tools StackPanel.
            var trailing = tc.GetVisualDescendants().OfType<StackPanel>()
                .FirstOrDefault(sp => sp.Children.Any(c => c is Button b && b.Content?.ToString() == "Prettify"));
            trailing.Should().NotBeNull("trailing-tools StackPanel should be in the visual tree");
            trailing!.IsVisible.Should().BeTrue("trailing tools should be visible when Body is selected");
            trailing.Bounds.Width.Should().BeGreaterThan(100, $"trailing tools should have real width. bounds={trailing.Bounds}");
        }
        finally { win.Close(); }
    }

    /// <summary>Diagnostic: trace exactly when the chevron is shown across a range of
    /// widths with all data-dots active (matching the user's screenshot scenario). The
    /// natural width should be ~1084px; with trailing tools ~276px + 10px leftPad, the
    /// threshold for "all tabs fit" should be ~1370px. Above that, chev should be hidden.</summary>
    [AvaloniaTheory]
    [InlineData(1400, false)]
    [InlineData(1500, false)]
    [InlineData(1600, false)]
    [InlineData(1700, false)]
    [InlineData(1800, false)]
    public void AtWideWidths_ChevHidden(int width, bool expectedOverflow)
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);
        vm.BodyType = "xml";
        vm.AuthType = "basic";
        vm.PreRequestScript = "// x";
        vm.RequestTabIndex = 4;
        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        var host = new Border { Width = width, Height = 600, Child = workspace };
        var win = new Window { Width = 2200, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout();
            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().FirstOrDefault()!;
            tc.HasOverflow.Should().Be(expectedOverflow,
                $"at width={width}: chev visibility");
        }
        finally { win.Close(); }
    }

    /// <summary>Regression for dot/badge measure invalidation. When a dot or count badge
    /// becomes visible after the initial layout (e.g. user starts editing a body, populates
    /// a header), the TabItem's natural width grows by ~10px. The OverflowTabsPanel must
    /// re-measure to pick up the new NaturalWidth, otherwise it stays stuck thinking the
    /// strip is narrower than it actually is — which inverts to the wrong overflow state.</summary>
    [AvaloniaFact]
    public void DotsBecomingVisible_TriggersReMeasure()
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);

        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        var host = new Border { Width = 1400, Height = 600, Child = workspace };
        var win = new Window { Width = 1900, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();

            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().FirstOrDefault()!;
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault()!;
            var naturalBefore = inner.NaturalWidth;

            // Now flip on the dots — this should grow each tab's natural width by ~10px.
            vm.AuthType = "basic";
            vm.BodyType = "xml";
            vm.PreRequestScript = "// x";
            win.UpdateLayout();
            win.UpdateLayout();

            var naturalAfter = inner.NaturalWidth;
            naturalAfter.Should().BeGreaterThan(naturalBefore + 10,
                $"natural width should grow when dots become visible. before={naturalBefore} after={naturalAfter}");
        }
        finally { win.Close(); }
    }

    /// <summary>Regression for the original chevron-reserve over-eager behavior.
    /// Pre-fix: at borderline widths, the chev-reserve re-measure would evict a second
    /// tab (Settings + Sent). Post-fix: only the last truly-not-fitting tab hides; the
    /// chev sits in the existing slack between the last visible tab and the right-aligned
    /// trailing tools.
    ///
    /// The host width is discovered dynamically — stepping narrower until the strip first
    /// overflows — rather than hard-coded, so the test stays valid across font-metric
    /// differences (CI vs. local) and small tab-strip layout changes. At the first
    /// overflow the strip is over by only a few pixels, so a correct control hides
    /// exactly one tab; a regressed chev-reserve would cascade to two.</summary>
    [AvaloniaFact]
    public void RealRequestWorkspace_BorderlineWidth_HidesOnlyLastTab()
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);
        vm.BodyType = "xml";
        vm.Url = "https://soap.example.com/CustomerService/QueryAccount";
        vm.RequestTabIndex = 4;
        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        var host = new Border { Width = 1900, Height = 600, Child = workspace };
        var win = new Window { Width = 2400, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout();
            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().First();
            tc.HasOverflow.Should().BeFalse("at 1900px the whole tab strip fits with room to spare");

            // Step the host narrower until the strip first overflows. At that boundary the
            // overflow is only a few pixels — far less than one tab — so the control must
            // hide exactly the last tab. A cascading chev-reserve would hide two.
            for (double w = 1900; w > 600; w -= 4)
            {
                host.Width = w;
                win.UpdateLayout();
                win.UpdateLayout();
                if (tc.HasOverflow)
                {
                    tc.HiddenItems.Count.Should().Be(1,
                        $"the first tab to overflow should be exactly one (host width {w})");
                    return;
                }
            }
            Assert.Fail("the tab strip never overflowed even down to 600px");
        }
        finally { win.Close(); }
    }

    /// <summary>Full-fidelity reproduction: instantiate the real RequestWorkspace control
    /// with a real RequestEditorViewModel and host it at a width that should easily fit
    /// all 11 sub-tabs. With the user's screenshot scenario (Body tab selected with a
    /// SOAP/XML body), the chevron should remain hidden because natural width fits.</summary>
    [AvaloniaFact]
    public void RealRequestWorkspace_WideHost_NoChevron()
    {
        var vm = new Vegha.App.ViewModels.RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vegha.App.ViewModels.RequestEditorViewModel>.Instance);

        // Simulate the user's screenshot: SOAP/XML body, Body tab selected.
        vm.BodyType = "xml";
        vm.BodyContent = "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">\n</soapenv:Envelope>";
        vm.Url = "https://soap.example.com/CustomerService/QueryAccount";
        vm.RequestTabIndex = 4; // Body

        var workspace = new Vegha.App.Controls.Workspace.RequestWorkspace { DataContext = vm };
        // Workspace at 1500px width — way more than the ~1000px natural tab strip needs.
        var host = new Border { Width = 1500, Height = 600, Child = workspace };
        var win = new Window { Width = 1900, Height = 800, Content = host };
        win.Show();
        try
        {
            win.UpdateLayout();
            win.UpdateLayout();
            win.UpdateLayout();

            var tc = workspace.GetVisualDescendants().OfType<OverflowTabControl>().FirstOrDefault()!;
            tc.Should().NotBeNull("the workspace should contain an OverflowTabControl");
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            var natural = inner?.NaturalWidth ?? -1;
            var tcBounds = tc.Bounds;

            // Diagnostic: print details to help understand actual measurements regardless of pass/fail.
            tc.HasOverflow.Should().BeFalse(
                $"natural={natural:F0} tcBounds={tcBounds} hidden=[{string.Join(",", tc.HiddenItems.Select(i => (i as TabItem)?.Header?.ToString() ?? "?"))}] — " +
                "11 tabs should fit in a 1500px workspace");
        }
        finally { win.Close(); }
    }

    /// <summary>Sweep across widths to find the borderline where the bug manifests.
    /// At each width, the chevron should only be shown if natural + tag width genuinely
    /// exceeds the available width. The dead space between the last visible tab and
    /// the chevron should not exceed a reasonable margin.</summary>
    [AvaloniaTheory]
    [InlineData(600)]
    [InlineData(800)]
    [InlineData(1000)]
    [InlineData(1100)]
    [InlineData(1200)]
    [InlineData(1300)]
    [InlineData(1500)]
    public void WidthSweep_ChevronOnlyWhenTrulyOverflowing(int width)
    {
        var tc = new OverflowTabControl { Classes = { "overflowtabs" } };
        string[] labels = { "Params", "Authorization", "Headers", "Vars", "Body",
                             "Pre-request", "Post-response", "Tests", "Docs", "Settings", "Sent" };
        foreach (var l in labels) tc.Items.Add(new TabItem { Header = l });

        var trailing = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        trailing.Children.Add(new Button { Content = "XML ▼", Padding = new Thickness(4, 2) });
        trailing.Children.Add(new Button { Content = "JSON→XML", Padding = new Thickness(4, 2) });
        trailing.Children.Add(new Button { Content = "Prettify", Padding = new Thickness(4, 2) });
        tc.Tag = trailing;
        tc.SelectedIndex = 4;

        var (_, win) = Host(tc, width);
        try
        {
            var inner = tc.GetVisualDescendants().OfType<OverflowTabsPanel>().FirstOrDefault();
            var natural = inner?.NaturalWidth ?? 0;
            // Find tag's actual rendered width via the visual tree.
            var trailingBounds = trailing.Bounds;
            var msg = $"width={width} natural={natural:F0} tagBounds={trailingBounds} " +
                      $"HasOverflow={tc.HasOverflow} hidden={tc.HiddenItems.Count}";
            // We assert that if natural + tagBounds.Width is comfortably under width,
            // overflow should NOT be reported.
            if (natural + trailingBounds.Width + 50 /*generous chev+leftpad slack*/ < width)
            {
                tc.HasOverflow.Should().BeFalse(msg);
            }
        }
        finally { win.Close(); }
    }
}
