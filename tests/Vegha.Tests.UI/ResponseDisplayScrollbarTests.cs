using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Regression tests for the response pane's vertical scrollbar — the user reported it
/// "follows the text" instead of staying anchored at the right edge of the viewport.
/// The bug surfaces with long single-line response bodies where horizontal overflow
/// causes the vertical scrollbar overlay to drift.
///
/// These tests load <see cref="ResponseDisplay"/> headlessly, push a long single-line
/// payload through <see cref="RequestEditorViewModel"/>, force layout, locate the
/// vertical <see cref="ScrollBar"/> in each tab's content tree, and assert its bounds
/// don't change when the content is scrolled horizontally.
/// </summary>
public class ResponseDisplayScrollbarTests
{
    // Response payload that mimics the user's reported scenario: many lines (so vertical
    // scrolling is needed) AND at least one line that's far wider than the viewport (so
    // horizontal scrolling is needed too). The combination is what surfaces the V-bar
    // drift bug — the bar only renders + becomes interactive when both axes scroll.
    private static readonly string LongSingleLineResponse = BuildBigResponse();

    private static string BuildBigResponse()
    {
        var sb = new System.Text.StringBuilder();
        // 200 short lines — guarantees vertical overflow at 600px window height.
        for (var i = 0; i < 200; i++)
            sb.AppendLine($"line {i:000}: lorem ipsum dolor sit amet");
        // One brutally long single line — guarantees horizontal overflow at 900px width.
        sb.Append(new string('A', 1200));
        sb.Append(new string('B', 1200));
        sb.AppendLine();
        // Trailing tail lines so the long line doesn't get clipped at the document end.
        for (var i = 0; i < 50; i++)
            sb.AppendLine($"tail {i:00}");
        return sb.ToString();
    }

    [AvaloniaFact]
    public void RawTab_VerticalScrollbar_DoesNotDriftOnHorizontalScroll()
    {
        var (window, _, display) = HostResponseDisplay(
            longBody: LongSingleLineResponse,
            tabIndex: ResponseTabIndex.Raw);

        // The Raw tab is implemented as nested ScrollViewers (outer = V-only, inner = H-only).
        // The OUTER ScrollViewer's vertical scrollbar is the one that must stay anchored.
        var rawOuter = display.FindControl<ScrollViewer>("RawOuterScroll");
        rawOuter.Should().NotBeNull("Raw tab must expose RawOuterScroll for layout assertions");
        rawOuter!.AllowAutoHide.Should().BeFalse(
            "AllowAutoHide=False ensures the V-scrollbar lives in a dedicated chrome column " +
            "instead of overlaying the content — that's what keeps it at the viewport edge");

        var verticalBars = FindVerticalScrollBars(rawOuter).ToList();
        verticalBars.Should().NotBeEmpty(
            "the outer V-only ScrollViewer must render a vertical scrollbar when content overflows");

        // Snapshot V-scrollbar bounds + outer ScrollViewer bounds in window coordinates.
        var beforeBarX = verticalBars[0].TranslatePoint(default, window)!.Value.X;
        var outerRight = rawOuter.TranslatePoint(new Point(rawOuter.Bounds.Width, 0), window)!.Value.X;

        // The V-scrollbar's right edge should be at (or very near) the outer ScrollViewer's
        // right edge — that's what "anchored at the viewport right edge" means.
        var beforeBarRight = beforeBarX + verticalBars[0].Bounds.Width;
        beforeBarRight.Should().BeApproximately(outerRight, 1.0,
            $"V-scrollbar right edge ({beforeBarRight:F1}) must align with the outer " +
            $"ScrollViewer's right edge ({outerRight:F1}) — that's the viewport edge");

        // The Raw tab is now backed by a CanvasTextView (ILogicalScrollable) inside a
        // single ScrollViewer. Push the outer ScrollViewer's horizontal offset right
        // and verify the vertical scrollbar didn't drift. Units are logical (chars)
        // because the child reports an ILogicalScrollable extent.
        var startX = rawOuter.Offset.X;
        rawOuter.Offset = new Vector(startX + 30, rawOuter.Offset.Y);
        window.UpdateLayout();

        var afterBarX = verticalBars[0].TranslatePoint(default, window)!.Value.X;
        afterBarX.Should().BeApproximately(beforeBarX, 0.5,
            $"V-scrollbar must stay anchored — drifted from X={beforeBarX:F1} to " +
            $"X={afterBarX:F1} after pushing horizontal offset");
    }

    [AvaloniaFact]
    public void BodyTab_DefaultsToWordWrap_So_NoHorizontalScrollNeeded()
    {
        // The Body tab uses AvaloniaEdit's TextEditor via ReadOnlyCodeView. AvaloniaEdit's
        // internal V-scrollbar can drift when content is wider than the viewport (the user-
        // reported "scrollbar following the text" bug). The fix is to default WordWrap=true
        // so horizontal overflow never arises. Verify the default.
        var (_, vm, display) = HostResponseDisplay(
            longBody: LongSingleLineResponse,
            tabIndex: ResponseTabIndex.Body);

        vm.ResponseWordWrap.Should().BeTrue(
            "the Body tab should default to word-wrap so long single-line responses don't " +
            "trigger AvaloniaEdit's V-scrollbar drift bug");

        var codeView = display.GetVisualDescendants().OfType<ReadOnlyCodeView>().FirstOrDefault();
        codeView.Should().NotBeNull("Body tab must contain a ReadOnlyCodeView");
        codeView!.WordWrap.Should().BeTrue(
            "ReadOnlyCodeView.WordWrap must be bound to ResponseWordWrap so the toggle takes effect");
    }

    [AvaloniaFact]
    public void BodyTab_ReadOnlyCodeView_FillsTabContentWidth()
    {
        // This is the canonical fix for the reported bug — see BodyEditor.axaml's leading
        // comment for the prior identical issue on the request side.
        //
        // The Body tab previously hosted ReadOnlyCodeView inside a DockPanel whose LAST
        // child was a PDF-preview Border. Avalonia's DockPanel auto-fills only the last
        // child; siblings without an explicit DockPanel.Dock default to Dock=Left and
        // size to their content width. Result: ReadOnlyCodeView was as wide as the
        // longest line of text, and AvaloniaEdit drew its V-scrollbar at the right edge
        // of THAT narrow control instead of at the right edge of the response pane.
        //
        // With the Grid fix, the visible body-mode child takes the full cell (Grid.Row=1)
        // and its width matches the response pane's content area. Verify that here.
        var (window, _, display) = HostResponseDisplay(
            longBody: LongSingleLineResponse,
            tabIndex: ResponseTabIndex.Body);

        var codeView = display.GetVisualDescendants().OfType<ReadOnlyCodeView>().FirstOrDefault();
        codeView.Should().NotBeNull("Body tab must host a ReadOnlyCodeView");

        // The Body tab content is the Grid we introduced — find it via the ReadOnlyCodeView's
        // direct parent (Grid).
        var parentGrid = codeView!.GetVisualParent() as Grid;
        parentGrid.Should().NotBeNull(
            "ReadOnlyCodeView must be a direct child of the Body tab's Grid (not a DockPanel) " +
            "so the editor fills the cell — see BodyEditor.axaml's leading comment for the " +
            "prior identical bug.");

        // The editor must occupy essentially the full width of its parent Grid's row 1
        // (toolbar lives in row 0). Allow a small slack for borders / padding.
        var gridWidth = parentGrid!.Bounds.Width;
        var editorWidth = codeView!.Bounds.Width;
        editorWidth.Should().BeGreaterThan(gridWidth - 4,
            $"ReadOnlyCodeView must fill the Body cell (editor={editorWidth:F1}, " +
            $"cell={gridWidth:F1}). When it's narrower than the cell, AvaloniaEdit draws its " +
            "V-scrollbar at the editor's right edge — inside the response pane, not at the " +
            "viewport edge. That's the user-reported 'scrollbar follows the text' bug.");
    }


    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>Tab indices in <see cref="ResponseDisplay"/>'s subtab TabControl.
    /// Body=0, Cookies=1, Headers=2, Test Results=3, Timeline=4, Raw=5.</summary>
    private enum ResponseTabIndex { Body = 0, Cookies = 1, Headers = 2, Tests = 3, Timeline = 4, Raw = 5 }

    private static (Window Window, RequestEditorViewModel Vm, ResponseDisplay Display)
        HostResponseDisplay(string longBody, ResponseTabIndex tabIndex)
    {
        // RequestEditorViewModel ctor needs HttpExecutor + OAuth2 + JintHost + logger —
        // none are exercised by the response viewer, so we wire minimal real instances.
        var http = new System.Net.Http.HttpClient();
        var vm = new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);

        vm.HasResponse = true;
        vm.ResponseStatusCode = 200;
        vm.ResponseStatusText = "OK";
        vm.ResponseContentType = "application/json";
        vm.ResponseBody = longBody;
        vm.RawResponseText = "HTTP/1.1 200 OK\nContent-Type: application/json\n\n" + longBody;
        vm.ResponseTabIndex = (int)tabIndex;

        var display = new ResponseDisplay { DataContext = vm };

        // Wrap in a Window so the visual tree has a real root and bounds — required for
        // TranslatePoint and for headless layout to produce meaningful coordinates.
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = display
        };
        window.Show();

        // Force two layout passes — first to materialize the templated children, second so
        // any width-dependent template parts (e.g. scrollbar visibility) settle.
        window.UpdateLayout();
        window.UpdateLayout();

        return (window, vm, display);
    }

    /// <summary>Finds every visible vertical ScrollBar in the visual tree under
    /// <paramref name="root"/>. ScrollBar is the template-part class used by ScrollViewer
    /// for both axes — we filter by orientation.</summary>
    private static IEnumerable<ScrollBar> FindVerticalScrollBars(Visual root)
    {
        foreach (var bar in root.GetVisualDescendants().OfType<ScrollBar>())
        {
            if (bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsVisible)
                yield return bar;
        }
    }

    /// <summary>Picks the innermost ScrollViewer under <paramref name="root"/> that has
    /// horizontal scrolling enabled (HorizontalScrollBarVisibility == Auto or Visible).
    /// Innermost = the one closest to the leaf content, which is the one whose Offset we
    /// need to push to simulate a user dragging the horizontal scrollbar.</summary>
    private static ScrollViewer? FindScrollViewerWithHorizontalScroll(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(sv => sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                      && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Hidden
                      && sv.Extent.Width > sv.Viewport.Width)
            .LastOrDefault();
}
