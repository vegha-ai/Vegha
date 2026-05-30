using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vegha.App.Controls.Services;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels.Settings;

namespace Vegha.App.Controls.Settings;

/// <summary>Modal Settings window. Hosts the nav rail + content pane defined in
/// <c>SettingsWindow.axaml</c>. Save/Cancel close with the result <c>true</c>/<c>false</c>;
/// the caller (MainWindow) consumes <see cref="SettingsWindowViewModel.Saved"/> to apply
/// live updates (theme, zoom, proxy, editor) without an app restart.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            this.RemoveMinimizeMaximize();
            ZoomHost.Attach(this);
        };
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private void OnSave_Click(object? sender, RoutedEventArgs e)
    {
        // SaveCommand fires before this handler thanks to Avalonia's command-binding order;
        // we just need to close the dialog with a positive result so the caller knows to apply.
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    // Drag the dialog by its themed title bar (no OS chrome to grab now that the client
    // area is extended). Left-button only so the close button still gets its clicks.
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
