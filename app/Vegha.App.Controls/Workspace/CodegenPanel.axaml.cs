using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;
using Vegha.Core.Codegen;

namespace Vegha.App.Controls.Workspace;

public partial class CodegenPanel : UserControl
{
    /// <summary>Fired when the user clicks the X in the panel header. The host
    /// (MainWindow) collapses the right column; the View menu offers a re-open.</summary>
    public event EventHandler? CloseRequested;

    public CodegenPanel()
    {
        InitializeComponent();
    }

    private async void OnCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CodegenViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(vm.Snippet);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
