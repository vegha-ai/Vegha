using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vegha.App.ViewModels;

/// <summary>
/// Drives the first-run welcome dialog. Buttons here delegate to host actions —
/// the dialog itself is plain content, the host wires "Open folder" / "Import" to
/// the existing flows. The Privacy section is the canonical statement: nothing
/// leaves the machine that the user didn't explicitly send.
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    public string PrivacyStatement =>
        "Vegha sends no telemetry, no crash reports, and no analytics. The only " +
        "outbound traffic is the requests you fire and the auth flows you start " +
        "(OAuth2 token exchanges, secret-manager calls). Nothing else.";

    public string Tagline =>
        "A native, MIT-licensed API testing app for Windows and macOS.";

    [ObservableProperty]
    private bool _dontShowAgain;

    public Action? OnOpenCollection { get; set; }
    public Action? OnImport { get; set; }
    public Action? OnTrySample { get; set; }
    public Action<bool>? OnDismiss { get; set; }

    [RelayCommand]
    private void OpenCollection()
    {
        OnOpenCollection?.Invoke();
        OnDismiss?.Invoke(DontShowAgain);
    }

    [RelayCommand]
    private void Import()
    {
        OnImport?.Invoke();
        OnDismiss?.Invoke(DontShowAgain);
    }

    [RelayCommand]
    private void TrySample()
    {
        OnTrySample?.Invoke();
        OnDismiss?.Invoke(DontShowAgain);
    }

    [RelayCommand]
    private void Dismiss() => OnDismiss?.Invoke(DontShowAgain);
}
