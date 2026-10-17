using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Git;
using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Settings → Git. Git executable (detect / custom / download), pull behavior,
/// default clone folder, proxy + TLS + Windows sign-in options, credential profiles per host,
/// SSH keys, and connection diagnostics.</summary>
public partial class GitSettingsViewModel : SettingsPageBase
{
    private readonly GitIntegration? _git;

    public override string Id => "git";
    public override string Title => "Git";
    public override string IconKey => "Git";

    public IReadOnlyList<GitChoice> PullModes { get; } = new[]
    {
        new GitChoice("rebase", "Rebase (keep a linear history; auto-stash local changes)"),
        new GitChoice("merge", "Merge (create a merge commit when branches diverge)"),
        new GitChoice("ff-only", "Fast-forward only (fail when branches diverge)"),
    };

    [ObservableProperty] private string _gitExecutablePath = string.Empty;
    [ObservableProperty] private GitChoice? _selectedPullMode;
    [ObservableProperty] private string _defaultCloneFolder = string.Empty;
    [ObservableProperty] private bool _useAppProxy = true;
    [ObservableProperty] private bool _useWindowsBroker = false;
    [ObservableProperty] private bool _azureReposOAuth = false;
    [ObservableProperty] private bool _useOsCertificateStore = true;
    [ObservableProperty] private bool _preferWindowsOpenSsh = true;
    [ObservableProperty] private string _enterpriseHostsText = string.Empty;
    [ObservableProperty] private bool _scanSecretsOnCommit = true;

    [ObservableProperty] private string _detectedGit = "Detecting…";
    [ObservableProperty] private bool _isGitMissing;
    [ObservableProperty] private string? _installStatus;
    [ObservableProperty] private bool _isInstalling;

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsAvailable => _git is not null;

    // ----- credential profiles ---------------------------------------------

    public ObservableCollection<GitCredentialProfile> Profiles { get; } = new();

    [ObservableProperty] private GitCredentialProfile? _selectedProfile;
    [ObservableProperty] private GitCredentialEditorViewModel? _editor;

    // ----- ssh ---------------------------------------------------------------

    public ObservableCollection<SshKeyInfo> SshKeys { get; } = new();
    [ObservableProperty] private SshKeyInfo? _selectedSshKey;
    [ObservableProperty] private string _newKeyName = "id_ed25519_vegha";
    [ObservableProperty] private string _newKeyComment = string.Empty;
    [ObservableProperty] private string _newKeyPassphrase = string.Empty;
    [ObservableProperty] private string? _sshStatus;

    // ----- diagnostics -------------------------------------------------------

    [ObservableProperty] private string _diagnosticsUrl = string.Empty;
    [ObservableProperty] private string _diagnosticsReport = string.Empty;
    [ObservableProperty] private bool _isDiagnosing;

    /// <summary>Wired by the view: copy text to the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Wired by the view: open a URL in the browser.</summary>
    public Action<string>? OpenUrl { get; set; }

    public GitSettingsViewModel(GitIntegration? git = null)
    {
        _git = git;
        if (_git is not null)
        {
            ReloadProfiles();
            ReloadSshKeys();
        }
        RefreshDetectedGit();
    }

    public override void ReadFrom(AppSettings s)
    {
        GitExecutablePath = s.GitExecutablePath ?? string.Empty;
        SelectedPullMode = PullModes.FirstOrDefault(p => p.Value == s.GitPullMode) ?? PullModes[0];
        DefaultCloneFolder = s.GitDefaultCloneFolder ?? string.Empty;
        UseAppProxy = s.GitUseAppProxy;
        UseWindowsBroker = s.GitUseWindowsBroker;
        AzureReposOAuth = s.GitAzureReposOAuth;
        UseOsCertificateStore = s.GitUseOsCertificateStore;
        PreferWindowsOpenSsh = s.GitPreferWindowsOpenSsh;
        EnterpriseHostsText = s.GitEnterpriseGitHubHosts is null ? string.Empty : string.Join("\n", s.GitEnterpriseGitHubHosts);
        ScanSecretsOnCommit = s.GitScanSecretsOnCommit;
    }

    public override AppSettings WriteTo(AppSettings e)
    {
        var hosts = EnterpriseHostsText.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/'))
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return e with
        {
            GitExecutablePath = string.IsNullOrWhiteSpace(GitExecutablePath) ? null : GitExecutablePath.Trim(),
            GitPullMode = SelectedPullMode?.Value ?? "rebase",
            GitDefaultCloneFolder = string.IsNullOrWhiteSpace(DefaultCloneFolder) ? null : DefaultCloneFolder.Trim(),
            GitUseAppProxy = UseAppProxy,
            GitUseWindowsBroker = UseWindowsBroker,
            GitAzureReposOAuth = AzureReposOAuth,
            GitUseOsCertificateStore = UseOsCertificateStore,
            GitPreferWindowsOpenSsh = PreferWindowsOpenSsh,
            GitEnterpriseGitHubHosts = hosts.Count == 0 ? null : hosts,
            GitScanSecretsOnCommit = ScanSecretsOnCommit,
        };
    }

    partial void OnGitExecutablePathChanged(string value) => RefreshDetectedGit();

    private void RefreshDetectedGit()
    {
        // Probe a typed custom path without touching the live locator (applied on Save).
        GitExecutable? exe;
        if (!string.IsNullOrWhiteSpace(GitExecutablePath))
        {
            var path = GitExecutablePath.Trim();
            var v = GitExecutableLocator.ReadVersion(path);
            exe = v is null ? null : new GitExecutable(path, v, GitExecutableSource.Custom, GitExecutableLocator.ParseVersion(v));
        }
        else exe = _git?.Locator.Current;
        IsGitMissing = exe is null;
        DetectedGit = exe is null
            ? "Git was not found."
            : $"{exe.VersionString} — {exe.Path} ({exe.Source switch { GitExecutableSource.Managed => "downloaded by Vegha", GitExecutableSource.Bundled => "bundled", GitExecutableSource.Custom => "custom path", _ => "system" }})"
              + (exe.MeetsMinimum ? string.Empty : $"  ⚠ Git {GitExecutableLocator.MinimumVersion} or newer is recommended");
    }

    [RelayCommand]
    private async Task InstallGitAsync()
    {
        if (_git is null) return;
        try
        {
            IsInstalling = true;
            var progress = new Progress<string>(s => InstallStatus = s);
            await _git.Installer.InstallAsync(progress, CancellationToken.None);
            _git.Locator.Invalidate();
            RefreshDetectedGit();
        }
        catch (Exception ex) { InstallStatus = "Install failed: " + ex.Message; }
        finally { IsInstalling = false; }
    }

    // ----- profiles ----------------------------------------------------------

    private void ReloadProfiles()
    {
        if (_git is null) return;
        var keepId = SelectedProfile?.Id ?? Editor?.Id;
        Profiles.Clear();
        foreach (var p in _git.Profiles.Load().Profiles.OrderBy(p => p.HostPattern, StringComparer.OrdinalIgnoreCase))
            Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == keepId);
    }

    partial void OnSelectedProfileChanged(GitCredentialProfile? value)
    {
        if (_git is null) return;
        if (value is null) { if (Editor is { IsNew: false }) Editor = null; return; }
        Editor = CreateEditor(value, null);
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (_git is null) return;
        SelectedProfile = null;
        Editor = CreateEditor(null, null);
    }

    private GitCredentialEditorViewModel CreateEditor(GitCredentialProfile? profile, string? host)
    {
        var editor = new GitCredentialEditorViewModel(_git!, profile, host) { OpenUrl = u => OpenUrl?.Invoke(u) };
        editor.Saved += (_, _) => ReloadProfiles();
        editor.Deleted += (_, _) => { Editor = null; ReloadProfiles(); };
        return editor;
    }

    // ----- ssh ---------------------------------------------------------------

    private void ReloadSshKeys()
    {
        if (_git is null) return;
        SshKeys.Clear();
        foreach (var k in _git.SshKeys.ListKeys()) SshKeys.Add(k);
    }

    [RelayCommand]
    private async Task GenerateSshKeyAsync()
    {
        if (_git is null) return;
        var name = string.IsNullOrWhiteSpace(NewKeyName) ? "id_ed25519_vegha" : NewKeyName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { SshStatus = "Invalid key file name."; return; }
        try
        {
            var path = Path.Combine(SshKeyService.SshDirectory, name);
            var key = await _git.SshKeys.GenerateAsync(path, string.IsNullOrWhiteSpace(NewKeyComment) ? Environment.UserName + "@vegha" : NewKeyComment.Trim(), NewKeyPassphrase);
            NewKeyPassphrase = string.Empty;
            ReloadSshKeys();
            SelectedSshKey = SshKeys.FirstOrDefault(k => k.PrivateKeyPath == key.PrivateKeyPath);
            if (CopyToClipboard is not null) await CopyToClipboard(key.PublicKeyText);
            SshStatus = $"Created {key.PrivateKeyPath}. Public key copied — add it to your Git host.";
        }
        catch (Exception ex) { SshStatus = ex.Message; }
    }

    [RelayCommand]
    private async Task CopyPublicKeyAsync()
    {
        if (SelectedSshKey is null || CopyToClipboard is null) return;
        await CopyToClipboard(SelectedSshKey.PublicKeyText);
        SshStatus = "Public key copied.";
    }

    [RelayCommand]
    private void RefreshSshKeys() => ReloadSshKeys();

    // ----- diagnostics -------------------------------------------------------

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (_git is null || string.IsNullOrWhiteSpace(DiagnosticsUrl)) return;
        try
        {
            IsDiagnosing = true;
            DiagnosticsReport = string.Empty;
            var report = await _git.Diagnostics.RunAsync(DiagnosticsUrl.Trim(), null, null, CancellationToken.None);
            DiagnosticsReport = report.Text;
        }
        catch (Exception ex) { DiagnosticsReport = "Diagnostics failed: " + ex.Message; }
        finally { IsDiagnosing = false; }
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (CopyToClipboard is not null && !string.IsNullOrEmpty(DiagnosticsReport))
            await CopyToClipboard(DiagnosticsReport);
    }
}

public sealed record GitChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
