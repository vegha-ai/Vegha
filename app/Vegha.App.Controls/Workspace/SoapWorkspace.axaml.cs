using Avalonia.Controls;
using Avalonia.Input;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class SoapWorkspace : UserControl
{
    public SoapWorkspace()
    {
        InitializeComponent();
        BodyEditor.KeyDown += OnBodyEditorKeyDown;
    }

    /// <summary>
    /// When the user pastes JSON into the SOAP body editor, convert it to XML using the
    /// loaded WSDL's schemas so child elements get the right namespaces. We don't suppress
    /// the native paste — instead we let it run and then asynchronously overwrite the body
    /// with the converted XML if the clipboard text was JSON-shaped and the conversion
    /// succeeded. If WSDL/operation aren't loaded, or the JSON can't be converted, the
    /// raw paste stays put (graceful fallback).
    /// </summary>
    private async void OnBodyEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var isPaste = (e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0)
            || (e.Key == Key.Insert && (e.KeyModifiers & KeyModifiers.Shift) != 0);
        if (!isPaste) return;
        if (DataContext is not SoapWorkspaceViewModel vm) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        string? text;
        try { text = await clipboard.GetTextAsync(); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        // Cheap shape check before paying to JSON-parse.
        var firstNonWs = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) { firstNonWs = i; break; }
        }
        if (firstNonWs < 0) return;
        var firstChar = text[firstNonWs];
        if (firstChar != '{' && firstChar != '[') return;

        if (vm.TryConvertJsonToBody(text, out var xml))
            vm.BodyXml = xml;
    }
}
