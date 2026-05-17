using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Regression test for the headers/params/vars row-width bug: when a row is rendered inside
/// the KvEditor's ItemsControl, it must stretch to fill the pane width — not collapse to the
/// natural width of its child controls. Avalonia's default ItemsControl wraps items in a
/// ContentPresenter whose HorizontalContentAlignment defaults to Stretch only if explicitly
/// set; without it the row shrinks to ~340px in a 1200px pane (visible as the "Headers row
/// squished to the left" bug). KvEditor.axaml sets both HorizontalAlignment AND
/// HorizontalContentAlignment to Stretch on the wrapping ContentPresenter — this test asserts
/// the resulting row width matches the pane width.
/// </summary>
public class KvEditorRowStretchTests
{
    [AvaloniaFact]
    public void HeadersRow_StretchesToFullPaneWidth()
    {
        var editor = new KvEditor
        {
            Mode = "headers",
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<KvEntry>
            {
                new("Content-Type", "text/xml", true),
            },
        };
        var window = new Window { Width = 1200, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            window.UpdateLayout();

            var row = editor.GetVisualDescendants().OfType<KvTableRow>().FirstOrDefault();
            row.Should().NotBeNull("the items control should have rendered a KvTableRow for the seeded entry");

            // The row should fill at least 80% of the pane's content width. We deliberately
            // don't require an exact match (margins/scrollbar reservations shave off a few
            // px); the bug we're guarding against showed the row at ~303px in a 1200px
            // pane (~25%) — root cause was a DockPanel last-child-fill conflict, where the
            // bulk-edit TextBox stole the fill slot from the rows ScrollViewer. The 80%
            // threshold catches any regression while tolerating reasonable layout overhead.
            row!.Bounds.Width.Should().BeGreaterThan(900,
                $"the row must span the editor pane width. Saw {row.Bounds.Width:F0}px in a 1200px pane.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ParamsRow_StretchesToFullPaneWidth()
    {
        // Same test for the default (non-headers) mode — same root cause, same risk.
        var editor = new KvEditor
        {
            Mode = "default",
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<KvEntry>
            {
                new("page", "1", true),
            },
        };
        var window = new Window { Width = 1200, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            window.UpdateLayout();

            var row = editor.GetVisualDescendants().OfType<KvTableRow>().FirstOrDefault();
            row.Should().NotBeNull();
            row!.Bounds.Width.Should().BeGreaterThan(900,
                "params rows must stretch — same root cause as headers rows.");
        }
        finally
        {
            window.Close();
        }
    }
}
