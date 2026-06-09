using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Services;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels;

/// <summary>Phases of the in-app update flow, surfaced to the banner.</summary>
public enum UpdateStatus
{
    /// <summary>Nothing happening; banner hidden.</summary>
    Idle,
    /// <summary>A check is in flight.</summary>
    Checking,
    /// <summary>The check found nothing newer.</summary>
    UpToDate,
    /// <summary>An update is downloading.</summary>
    Downloading,
    /// <summary>An update was downloaded and is staged to install.</summary>
    ReadyToRestart,
    /// <summary>The check or download failed.</summary>
    Failed,
}

/// <summary>Drives the VS Code-style update experience: a silent background check on startup
/// (and every few hours) that auto-downloads a newer release and then surfaces a single
/// "restart to update" banner. Also backs the Help → "Check for Updates…" command, where the
/// transient checking / up-to-date / failed states are shown too.
///
/// All Velopack interaction is behind <see cref="IUpdateService"/>, so this view-model carries
/// no dependency on the updater itself and is a harmless no-op on the store flavors (where
/// <see cref="IUpdateService.IsSupported"/> is false).</summary>
public partial class UpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>Public releases page — opened by the banner's "Release notes" link.</summary>
    public const string ReleaseNotesUrl = "https://github.com/vegha-ai/Vegha/releases";

    /// <summary>How often the background checker re-polls the feed while the app stays open.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    /// <summary>How long a transient "up to date" / "failed" banner lingers before auto-hiding.</summary>
    private static readonly TimeSpan TransientLinger = TimeSpan.FromSeconds(5);

    private readonly IUpdateService _service;
    private readonly AppSettingsStore _settings;
    private readonly SynchronizationContext? _ui;

    private Timer? _pollTimer;
    private Timer? _lingerTimer;
    private int _busy;            // 0/1 guard: at most one check/download flow at a time.
    private bool _stagedForExit;  // ApplyOnExit already called for the current staged update.

    public UpdateViewModel(IUpdateService service, AppSettingsStore settings)
    {
        _service = service;
        _settings = settings;
        // Captured on the UI thread at construction (DI builds this on the UI thread). Used to
        // marshal timer callbacks — which fire on thread-pool threads — back onto the UI.
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Whether this build can self-update at all. Bound by the menu to hide the
    /// "Check for Updates…" item on store builds.</summary>
    public bool IsSupported => _service.IsSupported;

    /// <summary>The running version, shown read-only on the Updates settings page.</summary>
    public string CurrentVersion => _service.CurrentVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsDownloading), nameof(IsReadyToRestart),
        nameof(IsUpToDate), nameof(IsFailed), nameof(IsBannerVisible))]
    private UpdateStatus _status = UpdateStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerTitle))]
    private string? _availableVersion;

    [ObservableProperty] private int _downloadProgress;

    [ObservableProperty] private string? _statusMessage;

    /// <summary>Set while a user-initiated check is running so the transient phases
    /// (checking / up-to-date / failed) surface in the banner. Background checks leave this
    /// false and only ever reveal the banner once an update is staged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private bool _isInteractive;

    /// <summary>User clicked "Later"/✕ on the ready-to-install banner. Reset when a new
    /// version is found so the next update re-announces itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private bool _isDismissed;

    public bool IsChecking => Status == UpdateStatus.Checking;
    public bool IsDownloading => Status == UpdateStatus.Downloading;
    public bool IsReadyToRestart => Status == UpdateStatus.ReadyToRestart;
    public bool IsUpToDate => Status == UpdateStatus.UpToDate;
    public bool IsFailed => Status == UpdateStatus.Failed;

    /// <summary>A staged update always shows; the transient phases show only during a
    /// user-initiated check. Dismiss hides whatever is currently shown.</summary>
    public bool IsBannerVisible =>
        !IsDismissed &&
        (Status == UpdateStatus.ReadyToRestart
         || (IsInteractive && Status is UpdateStatus.Checking or UpdateStatus.Downloading
             or UpdateStatus.UpToDate or UpdateStatus.Failed));

    public string BannerTitle =>
        string.IsNullOrEmpty(AvailableVersion) ? "Update available" : $"Vegha {AvailableVersion} is ready to install";

    // ─── Background lifecycle ───────────────────────────────────────────────

    /// <summary>Kicks an immediate silent check, then keeps polling on an interval. Called once
    /// by the host after first paint. No-op on builds that can't self-update.</summary>
    public void StartBackgroundChecks()
    {
        if (!_service.IsSupported) return;
        Post(() => _ = SilentCheckAsync());
        _pollTimer = new Timer(_ => Post(() => _ = SilentCheckAsync()), null, PollInterval, PollInterval);
    }

    private async Task SilentCheckAsync()
    {
        if (!_service.IsSupported || !_settings.Load().AutoCheckForUpdates) return;
        // Don't disturb an already-staged update or an in-flight flow.
        if (Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.ReadyToRestart) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            // A background poll is always silent — clear any lingering interactive state so the
            // transient checking/up-to-date phases don't flash in the banner.
            IsInteractive = false;
            await CheckAndMaybeDownloadAsync(interactive: false);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // ─── Commands ───────────────────────────────────────────────────────────

    /// <summary>Help → "Check for Updates…". Shows progress and the terminal result inline.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (!_service.IsSupported)
        {
            IsInteractive = true;
            IsDismissed = false;
            Status = UpdateStatus.Failed;
            StatusMessage = "This build manages its own updates through its store.";
            ScheduleTransientHide();
            return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            IsInteractive = true;
            IsDismissed = false;
            await CheckAndMaybeDownloadAsync(interactive: true);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Apply the staged update and relaunch immediately.</summary>
    [RelayCommand]
    private void RestartNow() => _service.ApplyAndRestart();

    /// <summary>"Later" / ✕ — stage the update to apply on the next normal exit and hide the
    /// banner. The user gets the new version simply by closing Vegha when they're done.</summary>
    [RelayCommand]
    private void Later()
    {
        if (Status == UpdateStatus.ReadyToRestart && !_stagedForExit)
        {
            _service.ApplyOnExit();
            _stagedForExit = true;
        }
        IsDismissed = true;
    }

    // ─── Core flow ──────────────────────────────────────────────────────────

    private async Task CheckAndMaybeDownloadAsync(bool interactive)
    {
        CancelTransientHide();
        Status = UpdateStatus.Checking;
        StatusMessage = "Checking for updates…";

        var beta = string.Equals(_settings.Load().UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase);
        var result = await _service.CheckForUpdatesAsync(beta);

        switch (result.Availability)
        {
            case UpdateAvailability.UpdateAvailable:
                AvailableVersion = result.NewVersion;
                IsDismissed = false;     // a fresh version re-arms the banner
                _stagedForExit = false;  // ...and a fresh on-exit staging
                Status = UpdateStatus.Downloading;
                DownloadProgress = 0;
                StatusMessage = $"Downloading Vegha {result.NewVersion}…";
                var ok = await _service.DownloadUpdateAsync(new Progress<int>(p => DownloadProgress = p));
                if (ok)
                {
                    DownloadProgress = 100;
                    Status = UpdateStatus.ReadyToRestart;
                    StatusMessage = $"Vegha {result.NewVersion} is ready — restart to finish updating.";
                }
                else
                {
                    Status = UpdateStatus.Failed;
                    StatusMessage = "The update downloaded with errors. Try again later.";
                    if (interactive) ScheduleTransientHide();
                }
                break;

            case UpdateAvailability.UpToDate:
                Status = UpdateStatus.UpToDate;
                StatusMessage = $"Vegha {CurrentVersion} is the latest version.";
                if (interactive) ScheduleTransientHide();
                break;

            case UpdateAvailability.NotSupported:
                Status = UpdateStatus.Idle;
                StatusMessage = null;
                break;

            default: // Failed
                Status = UpdateStatus.Failed;
                StatusMessage = "Couldn't check for updates. Check your connection and try again.";
                if (interactive) ScheduleTransientHide();
                break;
        }
    }

    // ─── Transient banner auto-hide (up-to-date / failed) ───────────────────

    private void ScheduleTransientHide()
    {
        CancelTransientHide();
        _lingerTimer = new Timer(_ => Post(() =>
        {
            // Only clear if we're still showing the same transient state (a newer flow may
            // have moved us on, e.g. to ReadyToRestart).
            if (Status is UpdateStatus.UpToDate or UpdateStatus.Failed)
            {
                Status = UpdateStatus.Idle;
                IsInteractive = false;
            }
        }), null, TransientLinger, Timeout.InfiniteTimeSpan);
    }

    private void CancelTransientHide()
    {
        _lingerTimer?.Dispose();
        _lingerTimer = null;
    }

    private void Post(Action action)
    {
        if (_ui is not null) _ui.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _lingerTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
