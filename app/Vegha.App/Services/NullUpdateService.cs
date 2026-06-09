using System.Reflection;
using Vegha.App.ViewModels.Services;

namespace Vegha.App.Services;

/// <summary>The updater used on flavors that must not self-update: Microsoft Store (MSIX) and
/// Mac App Store (MAS), where the store delivers updates. <see cref="IsSupported"/> is always
/// false, so the banner and the Help → "Check for Updates…" command stay hidden / inert.
/// Registered in place of <see cref="VelopackUpdateService"/> under the VEGHA_MSIX / VEGHA_MAS
/// compile flavors.</summary>
internal sealed class NullUpdateService : IUpdateService
{
    public bool IsSupported => false;

    public string CurrentVersion
    {
        get
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

    public Task<UpdateCheckResult> CheckForUpdatesAsync(bool includePrereleases, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateCheckResult.NotSupported);

    public Task<bool> DownloadUpdateAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public void ApplyAndRestart() { }

    public void ApplyOnExit() { }
}
