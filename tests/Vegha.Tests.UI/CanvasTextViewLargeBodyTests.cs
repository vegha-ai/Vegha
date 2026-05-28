using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Vegha.App.Controls.Workspace;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Regression tests for the user-reported freeze rendering 15 MB JSON responses with
/// pretty-printed line lengths up to ~450K chars (HTML email templates inlined as JSON
/// string values). Verifies CanvasTextView handles the worst case: a single-line raw
/// body, then a swap to pretty-printed content with mega-lines.
/// </summary>
public class CanvasTextViewLargeBodyTests
{
    private const string LargeFixturePath = @"C:\Temp\getNotificationTree.json";

    [AvaloniaFact]
    public void SetText_OneMegaLine_PaintsWithoutFreezing()
    {
        // Single-line 16 MB body — the worst case (initial paint before background
        // prettify finishes). DrawLine must clip to the visible horizontal window;
        // measuring 16M glyphs in a FormattedText would freeze indefinitely.
        var giantLine = new string('x', 16_000_000);
        AssertRenderFinishes(giantLine, budgetSeconds: 5);
    }

    [AvaloniaFact]
    public void SetText_ManyShortLines_PaintsWithoutFreezing()
    {
        // 100K short lines — the "happy path" pretty-printed JSON without mega-lines.
        var sb = new System.Text.StringBuilder(100_000 * 40);
        for (var i = 0; i < 100_000; i++)
            sb.AppendLine($"    \"key_{i:00000}\": \"value_{i:00000}\",");
        AssertRenderFinishes(sb.ToString(), budgetSeconds: 5);
    }

    [AvaloniaFact]
    public void SetText_PrettyPrintedWithEmbeddedMegaLines_PaintsWithoutFreezing()
    {
        // Mix of normal pretty-printed lines and 50 mega-lines of ~100K chars each —
        // mirrors the real notification-tree response shape (HTML email template
        // strings inlined inside the JSON).
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 1000; i++)
            sb.AppendLine($"    \"key_{i:0000}\": \"value\",");
        for (var i = 0; i < 50; i++)
        {
            sb.Append("    \"body\": \"");
            sb.Append(new string('A', 100_000));
            sb.AppendLine("\",");
        }
        AssertRenderFinishes(sb.ToString(), budgetSeconds: 5);
    }

    [AvaloniaFact]
    public void SetText_RealNotificationTreeFixture_PaintsWithoutFreezing()
    {
        // The actual user-reported payload. Skipped if the fixture isn't present so
        // CI environments without C:\Temp\getNotificationTree.json don't fail.
        if (!File.Exists(LargeFixturePath)) return;

        var text = File.ReadAllText(LargeFixturePath);
        AssertRenderFinishes(text, budgetSeconds: 10);
    }

    /// <summary>Mounts a CanvasTextView in a 900×600 window, sets Text, pumps the
    /// dispatcher until the off-thread split lands, forces a layout + render pass,
    /// and asserts the whole cycle finishes inside <paramref name="budgetSeconds"/>.
    /// A freeze means the dispatcher never returns — the assertion fires when the
    /// budget elapses without the build completing.</summary>
    private static void AssertRenderFinishes(string body, int budgetSeconds)
    {
        var view = new CanvasTextView
        {
            FontFamily = "Cascadia Mono, Consolas, Menlo, monospace",
            FontSize = 12,
            SyntaxKind = CanvasTextView.Syntax.Json,
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = view,
        };
        var window = new Window { Width = 900, Height = 600, Content = scroller };
        window.Show();
        window.UpdateLayout();

        var sw = Stopwatch.StartNew();
        view.Text = body;

        // Pump the dispatcher until either: (a) the off-thread split publishes its
        // _lines (Bounds non-zero + something to render), or (b) the budget elapses.
        // RunJobs processes queued continuations including the marshaled completion
        // from Task.Run inside SplitOffThreadAsync.
        var budget = TimeSpan.FromSeconds(budgetSeconds);
        while (sw.Elapsed < budget)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            // The first frame after the split publishes — _maxLineChars shows up in
            // Extent.Width. Use that as the "build landed" signal.
            if (((Avalonia.Controls.Primitives.ILogicalScrollable)view).Extent.Height > 0)
                break;
            Thread.Sleep(20);
        }

        sw.Stop();
        sw.Elapsed.Should().BeLessThan(budget,
            $"CanvasTextView must finish building + first-rendering a {body.Length:N0}-char body " +
            $"inside {budgetSeconds}s — anything longer is a freeze for the user.");

        // Force one more render to confirm we don't hang on the second paint either
        // (would catch a regression where DrawLine doesn't clip to visible window).
        var renderSw = Stopwatch.StartNew();
        view.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        renderSw.Stop();
        renderSw.ElapsedMilliseconds.Should().BeLessThan(1000,
            $"Second render pass took {renderSw.ElapsedMilliseconds}ms — DrawLine must clip to " +
            "the visible horizontal window or mega-lines will freeze every paint.");
    }
}
