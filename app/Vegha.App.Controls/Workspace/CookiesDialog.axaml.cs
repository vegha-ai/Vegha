using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Bruno-style cookie manager. Lists every cookie in the app-wide jar with
/// inline-editable Domain/Path/Name/Value/Expires + HttpOnly/Secure toggles, plus a
/// primary "Add cookie" action that seeds a new editable row. Reaches the cookies
/// state through <see cref="CookiesViewModel"/>, which the status-bar passes in.</summary>
public partial class CookiesDialog : Window
{
    private readonly CookiesViewModel? _vm;

    public CookiesDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public CookiesDialog(CookiesViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // Pull the latest state from the store as the dialog opens so newly arrived
        // Set-Cookie responses show up without a manual refresh click.
        vm.Refresh();
    }

    private void OnAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _ = _vm.AddCookieAsync();
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
