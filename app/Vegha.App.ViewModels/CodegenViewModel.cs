using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Codegen;

namespace Vegha.App.ViewModels;

/// <summary>Backs the right-pane code-snippet generator. Re-emits when the request changes
/// or the active tab switches; refreshes are debounced 250 ms so rapid keystrokes don't
/// thrash the emitter.</summary>
public partial class CodegenViewModel : ObservableObject
{
    private readonly RequestEditorViewModel _legacyEditor;
    private readonly OpenTabsViewModel? _openTabs;
    private RequestEditorViewModel? _boundEditor;

    /// <summary>Every registered emitter, in registry (Postman-alphabetical) order — the
    /// ItemsSource of the language dropdown.</summary>
    public IReadOnlyList<ICodegenEmitter> Emitters => CodegenRegistry.All;

    [ObservableProperty]
    private ICodegenEmitter _selectedEmitter;

    [ObservableProperty]
    private string _snippet = string.Empty;

    /// <summary>Debounce window (ms) for Refresh — set short enough that the user sees the
    /// snippet update as they type but long enough to avoid re-emitting on every keystroke.</summary>
    public int DebounceMs { get; set; } = 250;

    private System.Threading.Timer? _debounceTimer;
    private readonly object _debounceLock = new();

    public CodegenViewModel(RequestEditorViewModel legacyEditor, OpenTabsViewModel? openTabs = null)
    {
        _legacyEditor = legacyEditor;
        _openTabs = openTabs;
        _selectedEmitter = CodegenRegistry.Find("curl") ?? CodegenRegistry.All[0];

        if (_openTabs is not null)
        {
            _openTabs.PropertyChanged += OnOpenTabsChanged;
            BindEditor(ResolveActiveEditor());
        }
        else
        {
            BindEditor(_legacyEditor);
        }
    }

    private RequestEditorViewModel ResolveActiveEditor()
    {
        if (_openTabs?.ActiveTab is HttpRequestTabViewModel httpTab) return httpTab.Editor;
        return _legacyEditor;
    }

    private void OnOpenTabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenTabsViewModel.ActiveTab))
            BindEditor(ResolveActiveEditor());
    }

    private void BindEditor(RequestEditorViewModel editor)
    {
        if (_boundEditor is not null)
        {
            _boundEditor.PropertyChanged -= OnRequestChanged;
            _boundEditor.Headers.CollectionChanged -= OnCollectionChanged;
            _boundEditor.Params.CollectionChanged -= OnCollectionChanged;
        }
        _boundEditor = editor;
        _boundEditor.PropertyChanged += OnRequestChanged;
        _boundEditor.Headers.CollectionChanged += OnCollectionChanged;
        _boundEditor.Params.CollectionChanged += OnCollectionChanged;
        Refresh();  // immediate refresh on tab switch (no debounce)
    }

    private static readonly HashSet<string> RelevantProperties =
        new(StringComparer.Ordinal)
        {
            nameof(RequestEditorViewModel.Method),
            nameof(RequestEditorViewModel.Url),
            nameof(RequestEditorViewModel.BodyType),
            nameof(RequestEditorViewModel.BodyContent),
            nameof(RequestEditorViewModel.GraphQLQuery),
            nameof(RequestEditorViewModel.GraphQLVariables),
            nameof(RequestEditorViewModel.AuthType),
            nameof(RequestEditorViewModel.BearerToken),
            nameof(RequestEditorViewModel.BasicUsername),
            nameof(RequestEditorViewModel.BasicPassword),
            nameof(RequestEditorViewModel.ApiKeyName),
            nameof(RequestEditorViewModel.ApiKeyValue),
            nameof(RequestEditorViewModel.ApiKeyPlacement),
        };

    private void OnRequestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && RelevantProperties.Contains(e.PropertyName))
            ScheduleRefresh();
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => ScheduleRefresh();

    partial void OnSelectedEmitterChanged(ICodegenEmitter value)
    {
        Refresh();
        OnPropertyChanged(nameof(SyntaxHighlightingName));
    }

    /// <summary>AvaloniaEdit highlighter name for the active emitter's language. Used by the
    /// codegen panel's read-only editor; null falls back to plain text. Maps the emitter's
    /// short Language id onto the names AvaloniaEdit ships out of the box.</summary>
    public string? SyntaxHighlightingName => SelectedEmitter?.Language switch
    {
        // Families without a matching AvaloniaEdit highlighter map to the closest
        // brace/keyword-compatible one so snippets don't render as walls of plain text.
        "csharp"     => "C#",
        "go"         => "C#",         // no Go highlighter; C# is the closest brace-style approximation
        "swift"      => "C#",
        "java"       => "Java",
        "kotlin"     => "Java",
        "dart"       => "Java",
        "javascript" => "JavaScript",
        "python"     => "Python",
        "ruby"       => "Python",     // no Ruby highlighter; Python's string/comment shapes are closest
        "php"        => "PHP",
        "objc"       => "C++",
        "c"          => "C++",
        // PowerShell colorizes --flags and quoted strings — close enough for shell shapes
        // (curl / HTTPie / wget) that the snippet doesn't render as identical text.
        "curl"       => "PowerShell",
        "shell"      => "PowerShell",
        "powershell" => "PowerShell",
        _            => null,         // http, ocaml, r — plain text
    };

    private void ScheduleRefresh()
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ => Refresh(), null, DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    public void Refresh()
    {
        if (_boundEditor is null) return;
        try
        {
            var item = _boundEditor.BuildRequestItemFromVm();
            var vars = RequestEditorViewModel.MergeVariables(
                _boundEditor.EnvironmentVariables,
                RequestEditorViewModel.BuildVariableLookup(_boundEditor.Variables));
            var snippet = SelectedEmitter.Emit(item, vars);
            // Mask resolved secret values so a copied snippet doesn't leak them.
            Snippet = Vegha.Core.Interpolation.SecretRedactor.Redact(
                snippet, _boundEditor.SecretValuesForRedaction);
        }
        catch (Exception ex)
        {
            Snippet = "// Codegen failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CopySnippetAsync()
    {
        await Task.CompletedTask;
    }
}
