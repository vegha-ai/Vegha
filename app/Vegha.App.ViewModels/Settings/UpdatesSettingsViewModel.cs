using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Updates page VM — the auto-check toggle and the release channel. The current
/// version and whether this build can self-update are read-only, supplied by the host (they
/// originate in the update service, which the settings layer otherwise doesn't depend on).
/// On store builds / uninstalled dev runs the page shows an explanatory note instead.</summary>
public partial class UpdatesSettingsViewModel : SettingsPageBase
{
    public override string Id => "updates";
    public override string Title => "Updates";
    public override string IconKey => "CloudDownload";

    /// <summary>The running version, shown read-only.</summary>
    public string CurrentVersion { get; }

    /// <summary>False on store builds and dev runs that can't self-update; the page disables
    /// its controls and shows a "managed elsewhere" note.</summary>
    public bool IsSupported { get; }

    /// <summary>Inverse of <see cref="IsSupported"/> for binding the note's visibility.</summary>
    public bool IsNotSupported => !IsSupported;

    [ObservableProperty] private bool _autoCheck = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStable), nameof(IsBeta))]
    private string _channel = "stable";

    /// <summary>Two-way radio binding for the "stable" channel. Only acts on a true assignment
    /// (the radio group guarantees exactly one is selected).</summary>
    public bool IsStable
    {
        get => !IsBeta;
        set { if (value) Channel = "stable"; }
    }

    /// <summary>Two-way radio binding for the "beta" channel.</summary>
    public bool IsBeta
    {
        get => string.Equals(Channel, "beta", StringComparison.OrdinalIgnoreCase);
        set { if (value) Channel = "beta"; }
    }

    public UpdatesSettingsViewModel(string currentVersion, bool isSupported)
    {
        CurrentVersion = currentVersion;
        IsSupported = isSupported;
    }

    public override void ReadFrom(AppSettings settings)
    {
        AutoCheck = settings.AutoCheckForUpdates;
        Channel = string.IsNullOrWhiteSpace(settings.UpdateChannel) ? "stable" : settings.UpdateChannel;
    }

    public override AppSettings WriteTo(AppSettings existing) => existing with
    {
        AutoCheckForUpdates = AutoCheck,
        UpdateChannel = string.Equals(Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable",
    };
}
