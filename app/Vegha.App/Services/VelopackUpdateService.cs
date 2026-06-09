using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vegha.App.ViewModels.Services;
using Velopack;
using Velopack.Sources;

namespace Vegha.App.Services;

/// <summary>The Direct-download flavor's updater, backed by Velopack against the project's
/// GitHub Releases feed. Constructed lazily and defensively: every operation is a safe no-op
/// (rather than throwing) when the app isn't running from a Velopack install — e.g. a
/// <c>dotnet run</c> dev session or a portable unzip — so the UI can call it unconditionally.
///
/// Not registered on the MSIX / MAS flavors (see ServiceRegistration); those use
/// <see cref="NullUpdateService"/> because the store owns their updates.</summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>GitHub repository that hosts the Velopack release feed
    /// (RELEASES / releases.&lt;channel&gt;.json / *-full.nupkg attached to each Release).</summary>
    private const string DefaultRepoUrl = "https://github.com/vegha-ai/Vegha";

    // The feed source, auth token, and channel are overridable via environment variables so a
    // TEST build can point at a private sandbox repo (with a PAT), a local folder, and/or a
    // specific channel — without code edits and without publishing test releases to the public
    // repo. Each falls back to production behavior when unset, so a normal build is unaffected:
    //   VEGHA_UPDATE_FEED     local folder / file:// / http(s) feed  (default: the GitHub repo)
    //   VEGHA_UPDATE_REPO     GitHub repo URL                        (default: the public Vegha repo)
    //   VEGHA_UPDATE_TOKEN    PAT for a private repo                 (default: none — anonymous)
    //   VEGHA_UPDATE_CHANNEL  Velopack channel                       (default: this build's RID)
    private static string RepoUrl =>
        Environment.GetEnvironmentVariable("VEGHA_UPDATE_REPO") is { Length: > 0 } r ? r : DefaultRepoUrl;

    private static string? AccessToken =>
        Environment.GetEnvironmentVariable("VEGHA_UPDATE_TOKEN") is { Length: > 0 } t ? t : null;

    private static string? FeedOverride =>
        Environment.GetEnvironmentVariable("VEGHA_UPDATE_FEED") is { Length: > 0 } f ? f : null;

    /// <summary>Velopack channel for this build. Defaults to the running RID
    /// ("win-x64"/"win-arm64"/"osx-arm64"/"osx-x64"/"linux-x64") so a single GitHub Release can
    /// carry every platform's feed without the per-OS feed files colliding — it MUST match the
    /// <c>vpk pack --channel &lt;rid&gt;</c> used by eng/Pack-Installer.ps1. Overridable for tests.</summary>
    private static string ExplicitChannel =>
        Environment.GetEnvironmentVariable("VEGHA_UPDATE_CHANNEL") is { Length: > 0 } c ? c : CurrentRidChannel();

    /// <summary>Maps the running OS + process architecture onto the RID strings the release
    /// matrix and Pack-Installer use as Velopack channels.</summary>
    private static string CurrentRidChannel()
    {
        var os = OperatingSystem.IsWindows() ? "win"
               : OperatingSystem.IsMacOS() ? "osx"
               : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    private readonly ILogger<VelopackUpdateService> _log;

    private UpdateManager? _manager;
    private bool _managerPrerelease;
    private bool _managerBuilt;

    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> log) => _log = log;

    public bool IsSupported => Manager(false)?.IsInstalled == true;

    public string CurrentVersion
    {
        get
        {
            var v = Manager(false)?.CurrentVersion;
            return v?.ToString() ?? AssemblyVersion();
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        var mgr = Manager(includePrereleases);
        if (mgr is null || !mgr.IsInstalled)
        {
            _pending = null;
            return UpdateCheckResult.NotSupported;
        }

        try
        {
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _pending = null;
                return UpdateCheckResult.UpToDate;
            }
            _pending = info;
            return new UpdateCheckResult(UpdateAvailability.UpdateAvailable, info.TargetFullRelease?.Version?.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            _pending = null;
            return new UpdateCheckResult(UpdateAvailability.Failed, null, ex.Message);
        }
    }

    public async Task<bool> DownloadUpdateAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = _pending;
        var mgr = _manager;
        if (info is null || mgr is null || !mgr.IsInstalled) return false;

        try
        {
            await mgr.DownloadUpdatesAsync(info, p => progress?.Report(p), false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update download failed");
            return false;
        }
    }

    public void ApplyAndRestart()
    {
        var info = _pending;
        var mgr = _manager;
        if (info?.TargetFullRelease is null || mgr is null || !mgr.IsInstalled) return;

        try
        {
            // Hands off to Update.exe and exits this process; on success it does not return.
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying update + restart failed");
        }
    }

    public void ApplyOnExit()
    {
        var info = _pending;
        var mgr = _manager;
        if (info?.TargetFullRelease is null || mgr is null || !mgr.IsInstalled) return;

        try
        {
            // Spawn a waiter that applies the update once this process exits. silent = true (no
            // installer UI), restart = false (the user is closing the app — don't relaunch it).
            mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, true, false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Staging update for next exit failed");
        }
    }

    /// <summary>Builds (and caches) the <see cref="UpdateManager"/> for the requested channel.
    /// Rebuilds only when the prerelease flag flips, since that's baked into the source.
    /// Returns null if construction throws (treated as "can't update").</summary>
    private UpdateManager? Manager(bool prerelease)
    {
        if (_managerBuilt && _manager is not null && _managerPrerelease == prerelease)
            return _manager;

        try
        {
            var options = new UpdateOptions { ExplicitChannel = ExplicitChannel };
            var feed = FeedOverride;
            if (!string.IsNullOrEmpty(feed))
            {
                // Offline / sandbox testing of the in-app flow: a local folder, file:// URI, or
                // http(s) feed. The UpdateManager(string) ctor resolves a SimpleFileSource for a
                // path and a SimpleWebSource for a URL; normalize file:// to a local path first.
                var arg = Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : feed;
                _manager = new UpdateManager(arg, options, _log);
            }
            else
            {
                _manager = new UpdateManager(new GithubSource(RepoUrl, AccessToken, prerelease), options, _log);
            }
            _managerPrerelease = prerelease;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not initialize Velopack UpdateManager");
            _manager = null;
        }
        _managerBuilt = true;
        return _manager;
    }

    /// <summary>Assembly informational version (the <c>&lt;Version&gt;</c> from
    /// Directory.Build.props), minus any "+sha" SourceLink suffix — the fallback shown before
    /// Velopack reports an installed version.</summary>
    private static string AssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
