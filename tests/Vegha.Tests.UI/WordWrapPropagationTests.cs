using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Headless tests that the request + response body editors actually wrap by
/// default. Earlier we had property-level assertions (<c>ResponseWordWrap == true</c>,
/// <c>ReadOnlyCodeView.WordWrap == true</c>) but those passed even while AvaloniaEdit's
/// inner TextEditor didn't pick up the value. These tests reach into the inflated visual
/// tree and assert <see cref="TextEditor.WordWrap"/> directly — the property the actual
/// renderer reads.</summary>
public class WordWrapPropagationTests
{
    private static RequestEditorViewModel CreateVm()
    {
        var http = new HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    [AvaloniaFact]
    public void ResponseBody_AvaloniaEditTextEditor_HasWordWrapTrueByDefault()
    {
        // Host a ResponseDisplay with a textual response and check the AvaloniaEdit editor.
        var vm = CreateVm();
        vm.HasResponse = true;
        vm.ResponseContentType = "application/json";
        vm.ResponseBody = "{\"x\":1}";

        var display = new ResponseDisplay { DataContext = vm };
        var window = new Window { Width = 800, Height = 400, Content = display };
        window.Show();
        try
        {
            window.UpdateLayout();
            window.UpdateLayout();
            var editor = display.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
            editor.Should().NotBeNull("the Body tab should host an AvaloniaEdit TextEditor");
            editor!.WordWrap.Should().BeTrue(
                "the inner AvaloniaEdit TextEditor must end up with WordWrap=true so long " +
                "single-line responses don't horizontally scroll. Both the host's " +
                "ReadOnlyCodeView.WordWrap and the VM's ResponseWordWrap default to true.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RequestBody_VariableAwareTextEditor_HasWordWrapTrueWhenMultiLine()
    {
        // The body's VariableAwareTextEditor is set up with SingleLine="False" in BodyEditor.axaml.
        // Construct one directly with the same shape and assert wrap is on.
        var editorHost = new VariableAwareTextEditor
        {
            SingleLine = false,
            Text = "hello"
        };
        var window = new Window { Width = 600, Height = 200, Content = editorHost };
        window.Show();
        try
        {
            window.UpdateLayout();
            window.UpdateLayout();
            var editor = editorHost.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
            editor.Should().NotBeNull();
            editor!.WordWrap.Should().BeTrue(
                "multi-line VariableAwareTextEditors must default to WordWrap=true so the " +
                "request body wraps without an explicit toggle.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RequestBody_WordWrap_RemainsTrue_WhenInitiallyHiddenThenShown()
    {
        // Real BodyEditor has IsVisible="{Binding IsBodyRaw}" which starts false (no body type
        // picked → editor is collapsed). AvaloniaEdit's wrap layout only runs once the editor
        // becomes visible / laid out — if the WordWrap state is wrong at that moment, wrap
        // gets locked off until something forces re-layout. The Loaded + Dispatcher.Post
        // defense in VariableAwareTextEditor must cover this case.
        var editorHost = new VariableAwareTextEditor
        {
            SingleLine = false,
            IsVisible = false,    // mimics IsBodyRaw=false at startup
            Text = "long line of content that should wrap when visible"
        };
        var window = new Window { Width = 600, Height = 200, Content = editorHost };
        window.Show();
        try
        {
            window.UpdateLayout();

            // Now flip visible — like the user picking an XML body type.
            editorHost.IsVisible = true;
            window.UpdateLayout();
            window.UpdateLayout();

            // Drain the dispatcher queue so any Dispatcher.UIThread.Post handlers fire.
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var editor = editorHost.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
            editor.Should().NotBeNull();
            editor!.WordWrap.Should().BeTrue(
                "even when the host was initially collapsed and only became visible after " +
                "first layout, the editor's WordWrap must end up true. The Loaded + " +
                "Dispatcher.Post re-apply in the code-behind handles this.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RequestBody_WordWrap_RemainsTrue_AfterDeferredPropertySetters()
    {
        // Real-world XAML inflation: VariableAwareTextEditor is constructed first (with
        // default SingleLine=true), then attached to the visual tree, then the parent's
        // XAML setters fire — SingleLine="False" later. Plus syntax / text / variable
        // bindings resolve after DataContext arrives, which is yet later.
        // Reproduce that ordering and assert WordWrap is still true at the end.
        var editorHost = new VariableAwareTextEditor();      // SingleLine = true (default)
        var window = new Window { Width = 600, Height = 200, Content = editorHost };
        window.Show();
        try
        {
            window.UpdateLayout();

            // Now flip SingleLine + load text + apply syntax highlighter — mimicking the
            // XAML attribute pump that runs after the visual tree is attached.
            editorHost.SingleLine = false;
            editorHost.Text = "a very long line that should wrap when the viewport is narrow";
            editorHost.SyntaxHighlightingName = "XML";
            editorHost.ShowLineNumbers = true;
            editorHost.Bordered = false;
            window.UpdateLayout();
            window.UpdateLayout();

            var editor = editorHost.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
            editor.Should().NotBeNull();
            editor!.WordWrap.Should().BeTrue(
                "after all the deferred setters fire (matching the live XAML inflation order), " +
                "the inner AvaloniaEdit TextEditor.WordWrap must still be true. If this fails, " +
                "something downstream of SingleLine-flips-false is resetting WordWrap.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void VarsTotalCount_TracksBothCollections()
    {
        var vm = CreateVm();
        vm.VarsTotalCount.Should().Be(0);

        vm.Variables.Add(new KvEntry("a", "1"));
        vm.VarsTotalCount.Should().Be(1, "adding a pre-request var should bump the total");

        vm.PostResponseVariables.Add(new KvEntry("b", "res.getBody().token"));
        vm.VarsTotalCount.Should().Be(2, "adding a post-response var should also bump the total");

        vm.Variables.Clear();
        vm.VarsTotalCount.Should().Be(1, "removing pre-request rows should drop the total");
    }
}
