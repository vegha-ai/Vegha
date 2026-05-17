using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels.Settings;

namespace Vegha.App.Controls.Settings.Pages;

public partial class NetworkPage : UserControl
{
    /// <summary>Raised when the user clicks "Clear" under SSL session cache. The Settings
    /// window forwards this up so the host can call HttpExecutor.ResetConnectionPool().</summary>
    public event EventHandler? ClearSslPoolRequested;

    public NetworkPage()
    {
        InitializeComponent();
    }

    private void OnProxyModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode } && DataContext is NetworkSettingsViewModel vm)
            vm.ProxyMode = mode;
    }

    private void OnProxyProtocolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string p } && DataContext is NetworkSettingsViewModel vm)
            vm.ProxyProtocol = p;
    }

    private void OnClearSslPool_Click(object? sender, RoutedEventArgs e)
    {
        ClearSslPoolRequested?.Invoke(this, EventArgs.Empty);
    }
}
