using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Drives the Run-Sequence panel splitter with simulated pointer input and asserts the panel
/// resizes in BOTH directions — regression for "resize only works one way (to the left)".
/// </summary>
public class CollectionRunTabSplitterTests
{
    private static (Window Window, CollectionRunTab Tab, Grid SplitGrid, Border Splitter) Build()
    {
        var collection = new Collection
        {
            Name = "c",
            Requests = new List<RequestItem>
            {
                new() { Name = "R1", Method = "GET", Url = "http://x/1" },
                new() { Name = "R2", Method = "GET", Url = "http://x/2" },
            },
        };
        var vm = new CollectionRunTabViewModel(collection, "run:test", http: null!, scripting: null!);
        var tab = new CollectionRunTab { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = tab };
        window.Show();
        window.UpdateLayout();

        var splitGrid = tab.GetVisualDescendants().OfType<Grid>()
            .First(g => g.Name == "ConfigSplitGrid");
        var splitter = tab.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == "ConfigSplitter");
        return (window, tab, splitGrid, splitter);
    }

    private static Point CenterInWindow(Window window, Visual v)
    {
        var p = v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window);
        p.Should().NotBeNull();
        return p!.Value;
    }

    [AvaloniaFact]
    public void ConfigSplitter_drags_left_and_right()
    {
        var (window, _, grid, splitter) = Build();
        try
        {
            // Regression guard: the config grid must FILL the tab. It used to sit before the
            // results view in a DockPanel, silently docking Left at its minimum width — which
            // capped the splitter's rightward range ("resize only works to the left").
            grid.Bounds.Width.Should().BeGreaterThan(1000,
                "the config split grid must fill the tab width, not dock-left at its minimum");

            var start = CenterInWindow(window, splitter);
            var initial = grid.ColumnDefinitions[0].ActualWidth;
            initial.Should().BeGreaterThan(0);

            // Drag RIGHT by 150px — left panel must grow.
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 150, start.Y));
            window.MouseUp(new Point(start.X + 150, start.Y), MouseButton.Left);
            window.UpdateLayout();
            var afterRight = grid.ColumnDefinitions[0].ActualWidth;
            afterRight.Should().BeGreaterThan(initial + 100,
                "dragging the splitter right must widen the Run Sequence panel");

            // Drag LEFT by 200px from the splitter's new position — left panel must shrink.
            var start2 = CenterInWindow(window, splitter);
            window.MouseDown(start2, MouseButton.Left);
            window.MouseMove(new Point(start2.X - 200, start2.Y));
            window.MouseUp(new Point(start2.X - 200, start2.Y), MouseButton.Left);
            window.UpdateLayout();
            var afterLeft = grid.ColumnDefinitions[0].ActualWidth;
            afterLeft.Should().BeLessThan(afterRight - 150,
                "dragging the splitter left must narrow the Run Sequence panel");
        }
        finally { window.Close(); }
    }
}
