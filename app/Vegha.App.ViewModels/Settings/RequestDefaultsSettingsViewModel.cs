using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Request-defaults page VM — covers follow-redirects, SSL verify, cookies,
/// URL encoding, response-to-history persistence, and max body size.</summary>
public partial class RequestDefaultsSettingsViewModel : SettingsPageBase
{
    public override string Id => "requestdefaults";
    public override string Title => "Request defaults";
    public override string IconKey => "Send";

    [ObservableProperty] private bool _followRedirects = true;
    [ObservableProperty] private bool _verifySsl = true;
    [ObservableProperty] private bool _sendCookies = true;
    [ObservableProperty] private bool _saveCookies = true;
    [ObservableProperty] private bool _encodeUrl = true;
    [ObservableProperty] private bool _saveResponsesToHistory;
    [ObservableProperty] private int _maxBodySizeMb = 50;

    public override void ReadFrom(AppSettings s)
    {
        FollowRedirects = s.DefaultFollowRedirects;
        VerifySsl = s.DefaultVerifySsl;
        SendCookies = s.DefaultSendCookies;
        SaveCookies = s.DefaultSaveCookies;
        EncodeUrl = s.DefaultEncodeUrl;
        SaveResponsesToHistory = s.SaveResponsesToHistory;
        MaxBodySizeMb = s.MaxBodySizeMb;
    }

    public override AppSettings WriteTo(AppSettings e) => e with
    {
        DefaultFollowRedirects = FollowRedirects,
        DefaultVerifySsl = VerifySsl,
        DefaultSendCookies = SendCookies,
        DefaultSaveCookies = SaveCookies,
        DefaultEncodeUrl = EncodeUrl,
        SaveResponsesToHistory = SaveResponsesToHistory,
        MaxBodySizeMb = Math.Clamp(MaxBodySizeMb, 1, 2048),
    };
}
