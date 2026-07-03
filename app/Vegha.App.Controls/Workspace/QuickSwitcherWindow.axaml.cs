using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Alt+Tab-style overlay for switching collections. Opened by the host on Ctrl+E; once shown
/// it owns the chord:
/// <list type="bullet">
///   <item>Ctrl+E (or Ctrl+Tab) cycles forward, Ctrl+Shift+E / Ctrl+Shift+Tab cycles back.</item>
///   <item>Up / Down arrows browse (and cancel release-to-commit, so the overlay behaves like a
///     normal list once the user reaches for the arrows).</item>
///   <item>Enter commits, Esc cancels, releasing Ctrl commits (unless browsing).</item>
///   <item>Losing focus (Deactivated) cancels.</item>
/// </list>
/// The window resolves nothing itself — commit routing lives in <see cref="QuickSwitcherViewModel"/>.
/// </summary>
public partial class QuickSwitcherWindow : Window
{
    private readonly QuickSwitcherViewModel? _vm;

    // Once the user presses an arrow key we switch to "browse" mode: releasing Ctrl no longer
    // auto-commits, so they can take their time and click / Enter deliberately.
    private bool _browsing;
    private bool _committed;

    public QuickSwitcherWindow()
    {
        InitializeComponent();
    }

    public QuickSwitcherWindow(QuickSwitcherViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Deactivated += (_, _) => CancelAndClose();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.E or Key.Tab when ctrl:
                _vm.Move(shift ? -1 : +1);
                e.Handled = true;
                break;
            case Key.Down:
                _browsing = true;
                _vm.Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                _browsing = true;
                _vm.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitAndClose();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelAndClose();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        // Releasing Ctrl commits the current selection (Alt-Tab semantics) — unless the user
        // has switched to arrow-key browsing, where an explicit Enter/click is expected.
        if (_browsing) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin)
            CommitAndClose();
    }

    private void CommitAndClose()
    {
        if (_committed) return;
        _committed = true;
        _vm?.Commit();
        Close();
    }

    private void CancelAndClose()
    {
        if (_committed) return;
        _committed = true;
        Close();
    }
}
