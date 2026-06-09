using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Secrets;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Root VM for the new Settings window. Owns the navigation rail (one entry per
/// page), the currently-selected page, and the Save / RestoreDefaults / Cancel commands.
/// On Save it walks every page VM's <see cref="SettingsPageBase.WriteTo"/> and persists
/// the aggregated record in a single <see cref="AppSettingsStore.Save"/> call — the store
/// raises <c>Changed</c> exactly once so live-update consumers get a single notification.</summary>
public partial class SettingsWindowViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;

    public ObservableCollection<SettingsPageBase> Pages { get; } = new();

    [ObservableProperty] private SettingsPageBase? _selectedPage;

    public AppearanceSettingsViewModel Appearance { get; }
    public NetworkSettingsViewModel Network { get; }
    public SecretManagerSettingsViewModel SecretManager { get; }
    public RequestDefaultsSettingsViewModel RequestDefaults { get; }
    public EditorSettingsViewModel Editor { get; }
    public UpdatesSettingsViewModel Updates { get; }
    public ShortcutsSettingsViewModel Shortcuts { get; }

    /// <summary>Raised after a successful save so the host (MainWindow) can re-apply
    /// theme / zoom / proxy / editor settings live.</summary>
    public event EventHandler<AppSettings>? Saved;

    /// <summary>Raised when the user closes the dialog without saving.</summary>
    public event EventHandler? Cancelled;

    public SettingsWindowViewModel(
        AppSettingsStore store,
        IEnumerable<ShortcutRow> shortcutRows,
        Func<SecretProviderConfig, ISecretProvider?>? secretProviderFactory = null,
        string? appVersion = null,
        bool updatesSupported = false)
    {
        _store = store;

        Appearance = new AppearanceSettingsViewModel();
        Network = new NetworkSettingsViewModel();
        SecretManager = new SecretManagerSettingsViewModel(secretProviderFactory);
        RequestDefaults = new RequestDefaultsSettingsViewModel();
        Editor = new EditorSettingsViewModel();
        Updates = new UpdatesSettingsViewModel(string.IsNullOrWhiteSpace(appVersion) ? "—" : appVersion, updatesSupported);
        Shortcuts = new ShortcutsSettingsViewModel(shortcutRows);

        Pages.Add(Appearance);
        Pages.Add(Network);
        Pages.Add(SecretManager);
        Pages.Add(RequestDefaults);
        Pages.Add(Editor);
        Pages.Add(Updates);
        Pages.Add(Shortcuts);

        var current = _store.Load();
        foreach (var p in Pages) p.ReadFrom(current);

        SelectedPage = Appearance;
    }

    [RelayCommand]
    private void Save()
    {
        var s = _store.Load();
        foreach (var p in Pages) s = p.WriteTo(s);
        _store.Save(s);
        Saved?.Invoke(this, s);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var d = AppSettings.Default;
        foreach (var p in Pages) p.ReadFrom(d);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}
