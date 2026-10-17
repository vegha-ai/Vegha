using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;

namespace Vegha.App.ViewModels.Git;

/// <summary>Edits one <see cref="GitCredentialProfile"/>: host pattern, method (system / PAT /
/// SSH key / GitHub CLI / Azure CLI), PAT entry + where to store it, SSH key choice, and the
/// browser sign-in buttons for gh / az. Hosted by Settings → Git and by the "Configure
/// credentials" dialog the Source Control error banner opens.</summary>
public partial class GitCredentialEditorViewModel : ObservableObject
{
    private readonly GitIntegration _git;
    private string _id;

    public IReadOnlyList<GitAuthMethodOption> Methods { get; } = new[]
    {
        new GitAuthMethodOption(GitAuthMethod.System, "System Git (Credential Manager / SSH agent)", "Uses whatever works in your terminal. Git Credential Manager handles browser sign-in, SSO and MFA."),
        new GitAuthMethodOption(GitAuthMethod.Pat, "Personal access token", "Paste a token created on the provider's website."),
        new GitAuthMethodOption(GitAuthMethod.Ssh, "SSH key", "Use a specific private key for this host (SSH remotes)."),
        new GitAuthMethodOption(GitAuthMethod.GhCli, "GitHub CLI (gh)", "Reuse your `gh auth login` session — works with SSO-enforced orgs."),
        new GitAuthMethodOption(GitAuthMethod.AzCli, "Azure CLI (az)", "Use your `az login` Entra ID session for Azure DevOps (MFA / Conditional Access)."),
    };

    [ObservableProperty] private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatCreateUrl), nameof(PatHint), nameof(ProviderName), nameof(SshAddKeyUrl), nameof(IsGitHubHost), nameof(IsAzureDevOpsHost))]
    private string _hostPattern = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPat), nameof(IsSsh), nameof(IsGh), nameof(IsAz), nameof(MethodDescription))]
    private GitAuthMethodOption? _selectedMethod;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _secret = string.Empty;
    [ObservableProperty] private bool _storeInVegha;
    [ObservableProperty] private string _sshKeyPath = string.Empty;
    [ObservableProperty] private bool _hasStoredSecret;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isWorking;

    /// <summary>One-time code shown during `gh auth login --web`.</summary>
    [ObservableProperty] private string? _deviceCode;

    /// <summary>Remote URL used by "Test" (defaults to https://&lt;host&gt;).</summary>
    [ObservableProperty] private string? _testUrl;

    public bool IsPat => SelectedMethod?.Method == GitAuthMethod.Pat;
    public bool IsSsh => SelectedMethod?.Method == GitAuthMethod.Ssh;
    public bool IsGh => SelectedMethod?.Method == GitAuthMethod.GhCli;
    public bool IsAz => SelectedMethod?.Method == GitAuthMethod.AzCli;
    public string? MethodDescription => SelectedMethod?.Description;

    public bool IsGitHubHost => HostOnly is { } h && (GitHostKinds.IsGitHub(h) || GitProviderRegistry.ForHost(h) is GitHubProvider);
    public bool IsAzureDevOpsHost => HostOnly is { } h && GitHostKinds.IsAzureDevOps(h);
    public string ProviderName => HostOnly is { } h ? GitProviderRegistry.ForHost(h)?.Name ?? "Git host" : "Git host";
    public string? PatHint => HostOnly is { } h ? GitProviderRegistry.ForHost(h)?.PatHint : null;
    public string? PatCreateUrl => HostOnly is { } h && GitRemoteUrl.TryParse("https://" + HostPatternClean) is { } r
        ? GitProviderRegistry.ForHost(h)?.PatCreateUrl(r) : null;
    public string? SshAddKeyUrl => HostOnly is { } h ? SshKeyService.AddKeyUrl(h) : null;

    public bool IsGhInstalled => _git.Cli.Find("gh") is not null;
    public bool IsAzInstalled => _git.Cli.Find("az") is not null;

    /// <summary>Raised after save / delete so the host list refreshes.</summary>
    public event EventHandler? Saved;
    public event EventHandler? Deleted;

    /// <summary>Opens a URL in the browser (wired by the view).</summary>
    public Action<string>? OpenUrl { get; set; }

    public GitCredentialEditorViewModel(GitIntegration git, GitCredentialProfile? profile = null, string? hostHint = null)
    {
        _git = git;
        _id = profile?.Id ?? Guid.NewGuid().ToString("N");
        IsNew = profile is null;
        if (profile is not null)
        {
            DisplayName = profile.DisplayName;
            HostPattern = profile.HostPattern;
            Username = profile.Username ?? string.Empty;
            SshKeyPath = profile.SshKeyPath ?? string.Empty;
            StoreInVegha = profile.StoreMode == GitSecretStoreMode.VeghaIsolated;
            HasStoredSecret = profile.HasStoredSecret;
            SelectedMethod = Methods.First(m => m.Method == profile.Method);
        }
        else
        {
            HostPattern = hostHint ?? string.Empty;
            SelectedMethod = Methods[1];
        }
    }

    public bool IsNew { get; private set; }

    public string Id => _id;

    private string HostPatternClean
    {
        get
        {
            var p = HostPattern.Trim().TrimEnd('/');
            if (p.Contains("://", StringComparison.Ordinal)) p = p[(p.IndexOf("://", StringComparison.Ordinal) + 3)..];
            return p;
        }
    }

    private string? HostOnly
    {
        get
        {
            var p = HostPatternClean;
            if (p.Length == 0) return null;
            var slash = p.IndexOf('/');
            return (slash < 0 ? p : p[..slash]).ToLowerInvariant();
        }
    }

    public GitCredentialProfile ToProfile() => new()
    {
        Id = _id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? HostPatternClean : DisplayName.Trim(),
        HostPattern = HostPatternClean,
        Method = SelectedMethod?.Method ?? GitAuthMethod.System,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        SshKeyPath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath.Trim(),
        StoreMode = StoreInVegha ? GitSecretStoreMode.VeghaIsolated : GitSecretStoreMode.GitCredentialManager,
        HasStoredSecret = HasStoredSecret,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(HostPatternClean)) { SetStatus("Enter a host (e.g. github.com or dev.azure.com/contoso).", true); return; }
        try
        {
            IsWorking = true;
            var profile = ToProfile();
            if (profile.Method == GitAuthMethod.Pat && !string.IsNullOrEmpty(Secret))
            {
                await _git.SavePatAsync(profile, Secret.Trim());
                Secret = string.Empty;
                HasStoredSecret = profile.StoreMode == GitSecretStoreMode.VeghaIsolated;
            }
            else
            {
                if (profile.Method != GitAuthMethod.Pat) _git.Vault.Delete(profile.Id);
                _git.Profiles.Upsert(profile with { HasStoredSecret = profile.Method == GitAuthMethod.Pat && HasStoredSecret });
            }
            IsNew = false;
            SetStatus("Saved.", false);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { IsWorking = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            await _git.DeleteProfileAsync(ToProfile());
            Deleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    /// <summary>Saves, then runs <c>git ls-remote</c> against the test URL with this profile.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var url = string.IsNullOrWhiteSpace(TestUrl) ? null : TestUrl!.Trim();
        if (url is null)
        {
            SetStatus("Enter a repository URL to test against.", true);
            return;
        }
        await SaveAsync();
        if (StatusIsError) return;
        try
        {
            IsWorking = true;
            SetStatus("Testing…", false);
            var info = await _git.Service.LsRemoteAsync(url);
            SetStatus($"✓ Connected — {info.Branches.Count} branch(es){(info.DefaultBranch is null ? "" : $", default '{info.DefaultBranch}'")}.", false);
        }
        catch (GitOperationException ex) { SetStatus($"{ex.Error.Title}: {ex.Error.Explanation}", true); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { IsWorking = false; }
    }

    [RelayCommand]
    private async Task GhLoginAsync()
    {
        var host = HostOnly ?? "github.com";
        try
        {
            IsWorking = true;
            DeviceCode = null;
            SetStatus("Opening browser for GitHub sign-in…", false);
            var ok = await _git.Broker.GhLoginAsync(host, code => DeviceCode = code, null, CancellationToken.None);
            SetStatus(ok ? "✓ Signed in with GitHub CLI." : "GitHub CLI sign-in did not complete.", !ok);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { IsWorking = false; DeviceCode = null; }
    }

    [RelayCommand]
    private async Task AzLoginAsync()
    {
        try
        {
            IsWorking = true;
            SetStatus("Opening browser for Microsoft Entra sign-in…", false);
            var ok = await _git.Broker.AzLoginAsync(null, CancellationToken.None);
            SetStatus(ok ? "✓ Signed in with Azure CLI." : "Azure CLI sign-in did not complete.", !ok);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { IsWorking = false; }
    }

    [RelayCommand]
    private void OpenPatPage()
    {
        if (PatCreateUrl is { } u) OpenUrl?.Invoke(u);
    }

    [RelayCommand]
    private void OpenSshKeysPage()
    {
        if (SshAddKeyUrl is { } u) OpenUrl?.Invoke(u);
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }
}

public sealed record GitAuthMethodOption(GitAuthMethod Method, string Label, string Description)
{
    public override string ToString() => Label;
}
