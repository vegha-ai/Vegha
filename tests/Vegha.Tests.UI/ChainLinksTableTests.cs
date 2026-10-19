using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Scriptless;
using Vegha.Core.Domain;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>The linked-request cards: one card per link, and the "add" picker filters the
/// collection as you type so a request is chosen, not typed as a path.</summary>
public class ChainLinksTableTests
{
    private static Collection Collection() => new()
    {
        Name = "c",
        Requests = { new RequestItem { Name = "Health", Method = "GET" } },
        Folders =
        {
            new Folder
            {
                Name = "Outages",
                Requests =
                {
                    new RequestItem { Name = "Get Outages", Method = "GET" },
                    new RequestItem { Name = "Report Outages - PECO", Method = "POST" },
                    new RequestItem { Name = "Report Outages - BGE", Method = "POST" },
                },
            },
        },
    };

    [AvaloniaFact]
    public void Typing_in_the_add_picker_filters_and_enter_adds_the_link()
    {
        var rows = new ObservableCollection<ChainLinkRow> { ChainLinkRow.FromStorage(new KvPair("Health.bru", "always")), new() };
        // As the request editor and folder settings do: a filled ghost row spawns the next.
        Vegha.App.ViewModels.KvAutoAppend.Wire(rows, () => new ChainLinkRow(), r => r.IsBlank);
        var editor = new ChainLinksEditor(rows) { Candidates = ChainCandidate.Of(Collection(), null) };
        var table = new ChainLinksTable { Editor = editor };
        var window = new Window { Width = 900, Height = 600, Content = table };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            table.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("chainCard") && b.IsEffectivelyVisible)
                .Should().Be(1, "one card for the one link; the ghost row is just the add picker");

            var add = table.GetVisualDescendants().OfType<AutoCompleteBox>()
                .Single(b => b.IsEffectivelyVisible && b.DataContext is ChainLinkRow { IsBlank: true });
            add.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("rep bge");
            Dispatcher.UIThread.RunJobs();

            add.IsDropDownOpen.Should().BeTrue();
            // The drop-down lives in a popup root, outside the box's own visual tree.
            var popup = add.GetVisualDescendants().OfType<Popup>().Single();
            var list = popup.Child!.GetVisualDescendants().Prepend(popup.Child).OfType<ListBox>().Single();
            var listed = list.Items.Cast<ChainCandidate>().Select(c => c.Name);
            listed.Should().Equal("Report Outages - BGE");

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            rows.Where(r => !r.IsBlank).Select(r => r.ToKvPair()).Should().Equal(
                new KvPair("Health.bru", "always"), new KvPair("Outages/Report Outages - BGE.bru", "always"));
            rows[^1].IsBlank.Should().BeTrue("a new add picker follows");
        }
        finally
        {
            window.Close();
        }
    }
}
