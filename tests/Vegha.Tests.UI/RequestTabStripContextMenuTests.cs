using System;
using System.Linq;
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

/// <summary>Guards the per-tab right-click context menu: every rendered tab must carry a
/// MenuFlyout with the full action set (New Request, Clone, Rename, Revert, Save to collection,
/// and the Close family), and "Revert Changes" must enable/disable with the tab's dirty state.</summary>
public class RequestTabStripContextMenuTests
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

    private static (RequestTabStrip Strip, Window Win) Host(OpenTabsViewModel tabs)
    {
        var strip = new RequestTabStrip { DataContext = tabs };
        var win = new Window { Width = 900, Height = 60, Content = strip };
        win.Show();
        win.UpdateLayout();
        win.UpdateLayout();
        return (strip, win);
    }

    private static Border? FirstTabBorder(RequestTabStrip strip) =>
        strip.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("reqTab"));

    [AvaloniaFact]
    public void EachTab_HasContextMenu_WithExpectedItems()
    {
        var tabs = NewTabs();
        tabs.OpenOrActivate(
            new RequestItem { Name = "a", Method = "GET", Url = "https://x/", Kind = RequestKind.Http },
            "/A/a.bru", collectionPath: "/A");
        tabs.ActiveScope = "/A";

        var (strip, win) = Host(tabs);
        try
        {
            var border = FirstTabBorder(strip);
            border.Should().NotBeNull("the tab strip should render a reqTab Border per visible tab");

            var flyout = border!.ContextFlyout as MenuFlyout;
            flyout.Should().NotBeNull("each tab Border should carry a right-click MenuFlyout");

            var headers = flyout!.Items.OfType<MenuItem>()
                .Select(m => m.Header?.ToString())
                .ToList();
            headers.Should().ContainInOrder(
                "New Request", "Clone Request", "Rename…", "Revert Changes",
                "Save to collection…",
                "Close", "Close Others", "Close to the Left", "Close to the Right",
                "Close Saved", "Close All");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void RevertItem_DisabledWhenClean_EnabledWhenDirty()
    {
        var tabs = NewTabs();
        var tab = tabs.OpenOrActivate(
            new RequestItem { Name = "a", Method = "GET", Url = "https://x/", Kind = RequestKind.Http },
            "/A/a.bru", collectionPath: "/A");
        tabs.ActiveScope = "/A";

        var (strip, win) = Host(tabs);
        try
        {
            var border = FirstTabBorder(strip)!;
            var flyout = (MenuFlyout)border.ContextFlyout!;

            // The IsEnabled={Binding CanRevert} binding only activates once the flyout is shown
            // (its content inherits the Border's tab DataContext on open).
            flyout.ShowAt(border);
            win.UpdateLayout();
            var revert = flyout.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Revert Changes");
            revert.IsEnabled.Should().BeFalse("a saved/clean tab has nothing to revert");

            tab.IsDirty = true;
            win.UpdateLayout();
            revert.IsEnabled.Should().BeTrue("a dirty tab backed by a file can be reverted");

            flyout.Hide();
        }
        finally { win.Close(); }
    }
}
