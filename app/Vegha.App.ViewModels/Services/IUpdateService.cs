namespace Vegha.App.ViewModels.Services;

/// <summary>Outcome of an update check.</summary>
public enum UpdateAvailability
{
    /// <summary>The running build is already the newest available.</summary>
    UpToDate,

    /// <summary>A newer version was found and can be downloaded.</summary>
    UpdateAvailable,

    /// <summary>This build cannot self-update (a store flavor, a portable build, or a dev run
    /// that was never installed by Velopack). Callers should silently no-op.</summary>
    NotSupported,

    /// <summary>The check failed — offline, feed unreachable, etc.</summary>
    Failed,
}

/// <summary>Result of <see cref="IUpdateService.CheckForUpdatesAsync"/>.</summary>
/// <param name="Availability">What the check concluded.</param>
/// <param name="NewVersion">The available version (e.g. "1.2.0") when <paramref name="Availability"/>
/// is <see cref="UpdateAvailability.UpdateAvailable"/>; otherwise <c>null</c>.</param>
/// <param name="Error">A short failure reason when <paramref name="Availability"/> is
/// <see cref="UpdateAvailability.Failed"/>.</param>
public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string? NewVersion = null,
    string? Error = null)
{
    public static readonly UpdateCheckResult NotSupported = new(UpdateAvailability.NotSupported);
    public static readonly UpdateCheckResult UpToDate = new(UpdateAvailability.UpToDate);
}

/// <summary>Abstraction over the platform updater. The only real implementation is
/// <c>VelopackUpdateService</c> in the app project, which talks to Velopack — this interface
/// keeps the ViewModels layer free of any Velopack dependency. The Microsoft Store (MSIX) and
/// Mac App Store (MAS) flavors register a no-op implementation because those channels own the
/// update lifecycle themselves.</summary>
public interface IUpdateService
{
    /// <summary>True when this build can apply its own updates — i.e. the Direct-download
    /// flavor running from a Velopack install. False for store builds, portable builds, and
    /// uninstalled dev runs, in which case every other member is a safe no-op.</summary>
    bool IsSupported { get; }

    /// <summary>The running application version, e.g. "1.1.0".</summary>
    string CurrentVersion { get; }

    /// <summary>Queries the release feed. <paramref name="includePrereleases"/> maps the
    /// "beta" channel onto prerelease GitHub releases. Never throws — failures come back as
    /// <see cref="UpdateAvailability.Failed"/>. A successful "update available" result is
    /// remembered internally so <see cref="DownloadUpdateAsync"/> can act on it.</summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(bool includePrereleases, CancellationToken cancellationToken = default);

    /// <summary>Downloads the update found by the most recent successful check, reporting
    /// 0–100% progress. Returns <c>false</c> if there is nothing staged to download or the
    /// download failed.</summary>
    Task<bool> DownloadUpdateAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Applies the downloaded update and relaunches the app. Does not return on
    /// success (the process exits). No-op if nothing has been downloaded.</summary>
    void ApplyAndRestart();

    /// <summary>Stages the downloaded update to be applied the next time the app exits, without
    /// forcing a restart now. No-op if nothing has been downloaded.</summary>
    void ApplyOnExit();
}
