using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

public partial class RequestEditor : UserControl
{
    /// <summary>Bubbles up when the user clicks the codegen toggle button next to Save.
    /// MainWindow subscribes at the window level via <c>AddHandler</c> and flips the panel —
    /// a routed event is the path-independent way to reach the host from inside a
    /// DataTemplate-instantiated editor.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CodegenToggleRequestedEvent =
        RoutedEvent.Register<RequestEditor, RoutedEventArgs>(
            nameof(CodegenToggleRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> CodegenToggleRequested
    {
        add => AddHandler(CodegenToggleRequestedEvent, value);
        remove => RemoveHandler(CodegenToggleRequestedEvent, value);
    }

    private RequestEditorViewModel? _vm;

    public RequestEditor()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.VariablesSnapshotChanged -= OnVarsChanged;
        _vm = DataContext as RequestEditorViewModel;
        if (_vm is not null)
        {
            _vm.VariablesSnapshotChanged += OnVarsChanged;
            PushVariables();
        }
    }

    private void OnVarsChanged(object? sender, EventArgs e) => PushVariables();

    private void PushVariables()
    {
        if (_vm is null) return;
        UrlEditor.Variables = _vm.ResolveCurrentVariables();
    }

    private void OnToggleCodegen_Click(object? sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(CodegenToggleRequestedEvent));
    }
}
