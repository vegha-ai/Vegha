using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Services;
using Vegha.App.ViewModels.Settings;
using Vegha.Core.Persistence;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>A controllable <see cref="IUpdateService"/> stand-in so the view-model's flow can
/// be driven without touching Velopack or the network.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public bool IsSupported { get; set; } = true;
    public string CurrentVersion { get; set; } = "1.0.0";
    public UpdateCheckResult NextCheck { get; set; } = UpdateCheckResult.UpToDate;
    public bool DownloadResult { get; set; } = true;

    public int CheckCount;
    public int DownloadCount;
    public int ApplyAndRestartCount;
    public int ApplyOnExitCount;
    public bool? LastIncludePrereleases;

    public Task<UpdateCheckResult> CheckForUpdatesAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        LastIncludePrereleases = includePrereleases;
        return Task.FromResult(NextCheck);
    }

    public Task<bool> DownloadUpdateAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        DownloadCount++;
        progress?.Report(100);
        return Task.FromResult(DownloadResult);
    }

    public void ApplyAndRestart() => ApplyAndRestartCount++;
    public void ApplyOnExit() => ApplyOnExitCount++;
}

public class UpdatesSettingsViewModelTests
{
    [Fact]
    public void ReadFrom_then_WriteTo_round_trips_auto_check_and_channel()
    {
        var vm = new UpdatesSettingsViewModel("1.2.3", isSupported: true);

        vm.ReadFrom(AppSettings.Default with { AutoCheckForUpdates = false, UpdateChannel = "beta" });

        vm.AutoCheck.Should().BeFalse();
        vm.Channel.Should().Be("beta");
        vm.IsBeta.Should().BeTrue();
        vm.IsStable.Should().BeFalse();

        var written = vm.WriteTo(AppSettings.Default);
        written.AutoCheckForUpdates.Should().BeFalse();
        written.UpdateChannel.Should().Be("beta");
    }

    [Fact]
    public void Selecting_stable_radio_flips_channel_back_to_stable()
    {
        var vm = new UpdatesSettingsViewModel("1.2.3", isSupported: true);
        vm.ReadFrom(AppSettings.Default with { UpdateChannel = "beta" });

        vm.IsStable = true; // simulate clicking the Stable radio

        vm.Channel.Should().Be("stable");
        vm.IsBeta.Should().BeFalse();
        vm.WriteTo(AppSettings.Default).UpdateChannel.Should().Be("stable");
    }

    [Fact]
    public void Exposes_version_and_support_flags()
    {
        var vm = new UpdatesSettingsViewModel("9.9.9", isSupported: false);

        vm.CurrentVersion.Should().Be("9.9.9");
        vm.IsSupported.Should().BeFalse();
        vm.IsNotSupported.Should().BeTrue();
    }
}

public class UpdateViewModelTests
{
    static UpdateViewModelTests()
    {
        // Point AppSettingsStore at a throwaway dir so tests never read or stomp the real
        // user settings.json. Respect a dir the test runner already set (CI does).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VEGHA_SETTINGS_DIR")))
        {
            Environment.SetEnvironmentVariable(
                "VEGHA_SETTINGS_DIR",
                Path.Combine(Path.GetTempPath(), "vegha-update-tests-" + Guid.NewGuid().ToString("N")));
        }
    }

    private static AppSettingsStore StoreWith(bool autoCheck = true, string channel = "stable")
    {
        var store = new AppSettingsStore();
        // Save seeds the in-memory cache, so Load() returns these without a disk round-trip.
        store.Save(AppSettings.Default with { AutoCheckForUpdates = autoCheck, UpdateChannel = channel });
        return store;
    }

    [Fact]
    public async Task CheckNow_when_update_available_downloads_and_shows_restart_banner()
    {
        var svc = new FakeUpdateService { IsSupported = true, NextCheck = new UpdateCheckResult(UpdateAvailability.UpdateAvailable, "2.0.0") };
        using var vm = new UpdateViewModel(svc, StoreWith(channel: "stable"));

        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.Status.Should().Be(UpdateStatus.ReadyToRestart);
        vm.IsReadyToRestart.Should().BeTrue();
        vm.AvailableVersion.Should().Be("2.0.0");
        vm.IsBannerVisible.Should().BeTrue();
        svc.DownloadCount.Should().Be(1);
        svc.LastIncludePrereleases.Should().BeFalse();
    }

    [Fact]
    public async Task CheckNow_on_beta_channel_requests_prereleases()
    {
        var svc = new FakeUpdateService { IsSupported = true, NextCheck = UpdateCheckResult.UpToDate };
        using var vm = new UpdateViewModel(svc, StoreWith(channel: "beta"));

        await vm.CheckNowCommand.ExecuteAsync(null);

        svc.LastIncludePrereleases.Should().BeTrue();
        vm.Status.Should().Be(UpdateStatus.UpToDate);
        vm.IsBannerVisible.Should().BeTrue(); // interactive check surfaces the up-to-date result
    }

    [Fact]
    public async Task CheckNow_when_not_supported_reports_store_managed()
    {
        var svc = new FakeUpdateService { IsSupported = false };
        using var vm = new UpdateViewModel(svc, StoreWith());

        await vm.CheckNowCommand.ExecuteAsync(null);

        svc.CheckCount.Should().Be(0);
        vm.StatusMessage.Should().Contain("store");
    }

    [Fact]
    public async Task Later_after_ready_stages_on_exit_and_hides_banner()
    {
        var svc = new FakeUpdateService { IsSupported = true, NextCheck = new UpdateCheckResult(UpdateAvailability.UpdateAvailable, "2.0.0") };
        using var vm = new UpdateViewModel(svc, StoreWith());
        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.LaterCommand.Execute(null);

        svc.ApplyOnExitCount.Should().Be(1);
        vm.IsDismissed.Should().BeTrue();
        vm.IsBannerVisible.Should().BeFalse();
    }

    [Fact]
    public async Task RestartNow_applies_and_restarts()
    {
        var svc = new FakeUpdateService { IsSupported = true, NextCheck = new UpdateCheckResult(UpdateAvailability.UpdateAvailable, "2.0.0") };
        using var vm = new UpdateViewModel(svc, StoreWith());
        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.RestartNowCommand.Execute(null);

        svc.ApplyAndRestartCount.Should().Be(1);
    }

    [Fact]
    public void Ready_banner_shows_without_an_interactive_check_but_dismiss_hides_it()
    {
        var svc = new FakeUpdateService { IsSupported = true };
        using var vm = new UpdateViewModel(svc, StoreWith());

        // A background download reaches ReadyToRestart without IsInteractive — banner still shows.
        vm.Status = UpdateStatus.ReadyToRestart;
        vm.IsBannerVisible.Should().BeTrue();

        vm.IsDismissed = true;
        vm.IsBannerVisible.Should().BeFalse();
    }

    [Fact]
    public void Transient_phases_only_show_during_an_interactive_check()
    {
        var svc = new FakeUpdateService { IsSupported = true };
        using var vm = new UpdateViewModel(svc, StoreWith());

        vm.Status = UpdateStatus.Checking;
        vm.IsInteractive = false;
        vm.IsBannerVisible.Should().BeFalse(); // a silent background check stays hidden

        vm.IsInteractive = true;
        vm.IsBannerVisible.Should().BeTrue();
    }

    [Fact]
    public void StartBackgroundChecks_when_unsupported_does_nothing()
    {
        var svc = new FakeUpdateService { IsSupported = false };
        using var vm = new UpdateViewModel(svc, StoreWith());

        vm.StartBackgroundChecks();

        svc.CheckCount.Should().Be(0);
        vm.IsBannerVisible.Should().BeFalse();
    }
}
