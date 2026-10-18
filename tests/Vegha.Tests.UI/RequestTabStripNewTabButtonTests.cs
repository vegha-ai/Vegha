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

/// <summary>The "+" new-tab button used to live inside the tab ScrollViewer, so once the
/// request tabs overflowed it scrolled off the right edge. It must stay inside the strip's
/// visible bounds at any tab count, and sit right after the last tab when everything fits.</summary>
public class RequestTabStripNewTabButtonTests
{
    private static OpenTabsViewModel TabsWith(int count)
    {
        Func<RequestEditorViewModel> factory = () => new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
        var tabs = new OpenTabsViewModel(factory, NullLogger<OpenTabsViewModel>.Instance);
        for (int i = 0; i < count; i++)
            tabs.OpenOrActivate(
                new RequestItem { Name = $"Request number {i}", Method = "GET", Url = "https://x/", Kind = RequestKind.Http },
                $"/A/r{i}.bru", collectionPath: "/A");
        tabs.ActiveScope = "/A";
        return tabs;
    }

    private static (RequestTabStrip Strip, Button Plus, Window Win) Host(int tabCount)
    {
        var strip = new RequestTabStrip { DataContext = TabsWith(tabCount) };
        var win = new Window { Width = 700, Height = 60, Content = strip };
        win.Show();
        for (int i = 0; i < 3; i++) win.UpdateLayout();
        var plus = strip.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NewTabButton");
        return (strip, plus, win);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(12)]
    public void PlusButton_StaysWithinStrip(int tabCount)
    {
        var (strip, plus, win) = Host(tabCount);
        try
        {
            var topLeft = plus.TranslatePoint(new Point(0, 0), strip);
            topLeft.Should().NotBeNull();
            (topLeft!.Value.X + plus.Bounds.Width).Should().BeLessThanOrEqualTo(strip.Bounds.Width + 0.5,
                $"'+' must stay on screen with {tabCount} tabs (x={topLeft.Value.X}, strip={strip.Bounds.Width})");
            topLeft.Value.X.Should().BeGreaterThanOrEqualTo(0);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void PlusButton_SitsRightAfterLastTab_WhenTabsFit()
    {
        var (strip, plus, win) = Host(1);
        try
        {
            var tab = strip.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("reqTab"));
            var tabRight = tab.TranslatePoint(new Point(tab.Bounds.Width, 0), strip)!.Value.X;
            var plusLeft = plus.TranslatePoint(new Point(0, 0), strip)!.Value.X;
            (plusLeft - tabRight).Should().BeInRange(-0.5, 2, "'+' should be inline after the only tab");
        }
        finally { win.Close(); }
    }
}
