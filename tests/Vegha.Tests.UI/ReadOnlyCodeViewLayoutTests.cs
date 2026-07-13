using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Vegha.App.Controls.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Vegha.Tests.UI;

/// <summary>Guards the shared read-only code surface's layout. AvaloniaEdit forwards its own
/// <c>Padding</c> into its internal ScrollViewer, which shifts the line-number gutter into the
/// clipped region (digits render cut off at the left edge). The fix keeps the editor padding
/// zero and puts the inset on the host Border instead — this test locks that in.</summary>
public class ReadOnlyCodeViewLayoutTests
{
    private readonly ITestOutputHelper _output;
    public ReadOnlyCodeViewLayoutTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void Padding_Is_On_Host_Border_Not_Editor_And_Gutter_Present()
    {
        var view = new ReadOnlyCodeView { Text = "line one\nline two\nline three" };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();

        var editor = view.GetVisualDescendants().OfType<TextEditor>().First();
        var border = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == "HostBorder");

        _output.WriteLine($"editor.Padding={editor.Padding} border.Padding={border.Padding}");
        _output.WriteLine($"ShowLineNumbers={editor.ShowLineNumbers} LeftMargins={editor.TextArea.LeftMargins.Count}");

        // The inset must live on the Border so the gutter isn't clipped.
        Assert.Equal(default(Thickness), editor.Padding);
        Assert.True(border.Padding.Left >= 6, "host border needs a left inset so the gutter clears the edge");
        Assert.True(border.Padding.Top >= 4, "host border needs a top inset so the first line clears the toolbar");

        // Gutter still renders (line-number margin + separator).
        Assert.True(editor.ShowLineNumbers, "line numbers should be on");
        Assert.True(editor.TextArea.LeftMargins.Count > 0, "line-number margin missing");

        // A trailing spacer margin gives the code text a gap from the gutter separator.
        var spacer = editor.TextArea.LeftMargins[^1];
        _output.WriteLine($"last margin: {spacer.GetType().Name} width={spacer.Width}");
        Assert.IsType<Border>(spacer);
        Assert.True(spacer.Width >= 6, "text-gutter spacer margin missing or too thin");
    }
}
