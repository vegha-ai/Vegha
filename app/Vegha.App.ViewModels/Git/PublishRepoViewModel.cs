using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;

namespace Vegha.App.ViewModels.Git;

/// <summary>"Publish to Git": take a local workspace or collection folder to a remote.
/// Steps: choose scope → <c>git init</c> (only when not already inside a repo) + root
/// <c>.gitignore</c> → secret scan + initial commit → remote (create on GitHub / Azure DevOps
/// via API, or paste an existing URL) → <c>push -u</c>. When the remote already has history
/// the user can rebase their files onto it.</summary>
public partial class PublishRepoViewModel : ObservableObject
{
    private readonly GitIntegration _git;
    private CancellationTokenSource? _cts;

    public string? WorkspaceFolder { get; }
    public string? CollectionFolder { get; }

    /// <summary>Repo that already encloses the chosen scope (then we only add a remote + push).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsInit), nameof(ScopeSummary))]
    private GitRepoContext? _existingRepo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeFolder), nameof(ScopeSummary), nameof(NeedsInit))]
    private bool _publishWorkspace;

    public bool CanPublishWorkspace => !string.IsNullOrEmpty(WorkspaceFolder);
    public string? ScopeFolder => PublishWorkspace ? WorkspaceFolder : CollectionFolder;
    public bool NeedsInit => ExistingRepo is null;

    public string ScopeSummary => ExistingRepo is { } r
        ? $"Already a Git repository at {r.RepoRoot}. Vegha will connect it to a remote and push."
        : $"Vegha will initialize a repository in {ScopeFolder}.";

    // ----- remote choice -----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateMode))]
    private bool _useExistingUrl = true;

    public bool IsCreateMode => !UseExistingUrl;

    [ObservableProperty] private string _remoteUrl = string.Empty;
    [ObservableProperty] private string _remoteName = "origin";
    [ObservableProperty] private string _initialBranch = "main";
    [ObservableProperty] private string _commitMessage = "Initial commit from Vegha";

    // Create-on-provider
    [ObservableProperty] private string _accountUrl = "https://github.com";
    public ObservableCollection<GitProviderOwner> Owners { get; } = new();
    [ObservableProperty] private GitProviderOwner? _selectedOwner;
    [ObservableProperty] private string _newRepoName = string.Empty;
    [ObservableProperty] private bool _newRepoPrivate = true;
    [ObservableProperty] private string? _ssoAuthorizationUrl;

    // ----- progress ----------------------------------------------------------

    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private GitErrorInfo? _errorInfo;
    [ObservableProperty] private bool _isDone;

    /// <summary>Set when the remote has history unrelated to ours — offer to rebase onto it.</summary>
    [ObservableProperty] private bool _offerRebaseOntoRemote;

    /// <summary>Host shows the secret-scan confirmation dialog; true = commit anyway.</summary>
    public Func<IReadOnlyList<GitSecretFinding>, Task<bool>>? ConfirmSecretsAsync { get; set; }

    /// <summary>Host shows the identity dialog when user.name/email are missing; returns (name, email) or null.</summary>
    public Func<Task<(string Name, string Email)?>>? RequestIdentityAsync { get; set; }

    public event EventHandler<string>? Published;
    public event EventHandler<string>? ConfigureCredentialsRequested;

    public PublishRepoViewModel(GitIntegration git, string? workspaceFolder, string? collectionFolder)
    {
        _git = git;
        WorkspaceFolder = workspaceFolder;
        CollectionFolder = collectionFolder;
        // Default to the workspace when the collection lives inside it — one repo for the
        // whole workspace is what teams usually want (environments + scripts travel too).
        PublishWorkspace = CanPublishWorkspace && (CollectionFolder is null || IsUnder(CollectionFolder, WorkspaceFolder!));
        NewRepoName = Path.GetFileName((ScopeFolder ?? "vegha-collections").TrimEnd('\\', '/'));
        ExistingRepo = GitRepoLocator.Locate(ScopeFolder);
    }

    partial void OnPublishWorkspaceChanged(bool value)
    {
        ExistingRepo = GitRepoLocator.Locate(ScopeFolder);
        NewRepoName = Path.GetFileName((ScopeFolder ?? "vegha-collections").TrimEnd('\\', '/'));
    }

    // ----- provider ----------------------------------------------------------

    [RelayCommand]
    private async Task LoadOwnersAsync()
    {
        var provider = GitProviderRegistry.For(NormalizeAccount(AccountUrl));
        if (provider is null) { Error = "Repository creation is available for GitHub and Azure DevOps. Create the repo on your host and paste its URL."; return; }
        try
        {
            IsWorking = true;
            Error = null;
            SsoAuthorizationUrl = null;
            Owners.Clear();
            var account = await GetAccountAsync();
            if (account is null) return;
            foreach (var o in await provider.ListOwnersAsync(account, CancellationToken.None)) Owners.Add(o);
            SelectedOwner = Owners.FirstOrDefault();
        }
        catch (GitProviderException ex) { Error = ex.Message; SsoAuthorizationUrl = ex.SsoAuthorizationUrl; }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsWorking = false; }
    }

    private async Task<GitProviderAccount?> GetAccountAsync()
    {
        var baseUrl = NormalizeAccount(AccountUrl);
        var token = await _git.Broker.GetApiTokenAsync(baseUrl, null, _git.Credentials, CancellationToken.None);
        if (token is null)
        {
            Error = "Not signed in for this host. Configure credentials (PAT, GitHub CLI, Azure CLI), then retry.";
            return null;
        }
        return new GitProviderAccount(baseUrl, token.Value.Scheme, token.Value.Token);
    }

    private static string NormalizeAccount(string url)
    {
        var u = url.Trim().TrimEnd('/');
        return u.Contains("://", StringComparison.Ordinal) ? u : "https://" + u;
    }

    [RelayCommand]
    private void ConfigureCredentials()
    {
        var parsed = GitRemoteUrl.TryParse(UseExistingUrl && !string.IsNullOrWhiteSpace(RemoteUrl) ? RemoteUrl : NormalizeAccount(AccountUrl));
        if (parsed is null) return;
        var host = parsed.Host;
        if (GitHostKinds.IsAzureDevOps(host) && AzureDevOpsProvider.Organization(parsed) is { } org) host = "dev.azure.com/" + org;
        ConfigureCredentialsRequested?.Invoke(this, host);
    }

    // ----- publish -----------------------------------------------------------

    [RelayCommand]
    private async Task PublishAsync()
    {
        var folder = ExistingRepo?.RepoRoot ?? ScopeFolder;
        if (string.IsNullOrEmpty(folder)) { Error = "Nothing to publish."; return; }
        if (string.IsNullOrWhiteSpace(RemoteName)) RemoteName = "origin";

        IsWorking = true;
        Error = null;
        ErrorInfo = null;
        OfferRebaseOntoRemote = false;
        _cts = new CancellationTokenSource();
        var svc = _git.Service;
        var progress = new Progress<string>(l => ProgressText = l);
        try
        {
            // 1. init (never nested)
            if (ExistingRepo is null)
            {
                ProgressText = "Initializing repository…";
                await svc.InitAsync(folder, string.IsNullOrWhiteSpace(InitialBranch) ? null : InitialBranch.Trim(), _cts.Token);
                ExistingRepo = GitRepoLocator.Locate(folder);
            }
            WorkspaceBootstrapper.EnsureRepoGitIgnore(folder);
            if (CollectionFolder is not null && !PublishWorkspace) WorkspaceBootstrapper.EnsureCollectionGitIgnore(CollectionFolder);

            // 2. identity
            var (name, email) = await Task.Run(() => svc.GetIdentity(folder));
            if ((string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email)) && RequestIdentityAsync is not null)
            {
                var ident = await RequestIdentityAsync();
                if (ident is null) { Error = "A Git author name and email are required to commit."; return; }
                await Task.Run(() => svc.SetIdentity(folder, ident.Value.Name, ident.Value.Email));
            }

            // 3. initial commit (if there's anything to commit)
            await Task.Run(() => svc.StageAll(folder));
            var staged = await Task.Run(() => svc.Status(folder).Any(c => c.IsStaged));
            if (staged)
            {
                if (ConfirmSecretsAsync is not null)
                {
                    var findings = await Task.Run(() => GitSecretScanner.ScanStaged(svc, folder));
                    if (findings.Count > 0 && !await ConfirmSecretsAsync(findings))
                    {
                        Error = "Publishing stopped — review the flagged values (or move them to environment secrets) and try again.";
                        return;
                    }
                }
                ProgressText = "Committing…";
                await Task.Run(() =>
                {
                    var sig = svc.GetSignature(folder);
                    svc.Commit(folder, string.IsNullOrWhiteSpace(CommitMessage) ? "Initial commit from Vegha" : CommitMessage,
                        sig?.Name ?? "Vegha user", sig?.Email ?? "user@vegha.local");
                });
            }

            // 4. remote
            var url = await ResolveRemoteUrlAsync();
            if (url is null) return;
            var remotes = await Task.Run(() => svc.Remotes(folder));
            if (remotes.FirstOrDefault(r => r.Name == RemoteName) is { } existingRemote)
            {
                if (!string.Equals(existingRemote.Url, url, StringComparison.Ordinal))
                    await Task.Run(() => svc.UpdateRemoteUrl(folder, RemoteName, url));
            }
            else await Task.Run(() => svc.AddRemote(folder, RemoteName, url));

            // 5. push
            ProgressText = "Pushing…";
            await PushOrOfferRebaseAsync(folder, progress);
        }
        catch (OperationCanceledException) { ProgressText = "Cancelled."; }
        catch (GitOperationException ex) { ShowError(ex.Error); }
        catch (GitProviderException ex) { Error = ex.Message; SsoAuthorizationUrl = ex.SsoAuthorizationUrl; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsWorking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task PushOrOfferRebaseAsync(string folder, IProgress<string> progress)
    {
        try
        {
            await _git.Service.PushAsync(folder, RemoteName, branch: null, setUpstream: true, progress, _cts!.Token);
            Done(folder);
        }
        catch (GitOperationException ex) when (ex.Kind == GitErrorKind.NonFastForward)
        {
            // Remote isn't empty (e.g. created with a README). Offer to rebase onto it.
            OfferRebaseOntoRemote = true;
            ShowError(new GitErrorInfo(GitErrorKind.UnrelatedHistories, "The remote already has commits",
                "The remote repository isn't empty. Rebase your files on top of its history and push, or cancel and choose an empty repository.",
                Array.Empty<GitErrorAction>()));
        }
    }

    /// <summary>Fetches the remote branch, rebases local commits on top of it
    /// (<c>--allow-unrelated-histories</c> semantics via rebase --onto), then pushes.</summary>
    [RelayCommand]
    private async Task RebaseOntoRemoteAsync()
    {
        var folder = ExistingRepo?.RepoRoot;
        if (folder is null) return;
        IsWorking = true;
        Error = null;
        ErrorInfo = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(l => ProgressText = l);
        try
        {
            var branch = _git.Service.CurrentBranch(folder);
            await _git.Service.FetchAsync(folder, RemoteName, progress, _cts.Token);
            var env = new GitEnvironment().Set("GIT_EDITOR", "true");
            var r = await _git.Runner.RunAsync(folder, new[] { "pull", "--rebase", "--allow-unrelated-histories", "--autostash", RemoteName, branch },
                new GitRunOptions { Environment = env, OnProgress = l => ProgressText = l }, _cts.Token);
            if (!r.Success) throw new GitOperationException("pull", r.ExitCode, r.StdErr);
            OfferRebaseOntoRemote = false;
            await PushOrOfferRebaseAsync(folder, progress);
        }
        catch (GitOperationException ex) { ShowError(ex.Error); }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsWorking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<string?> ResolveRemoteUrlAsync()
    {
        if (UseExistingUrl)
        {
            if (string.IsNullOrWhiteSpace(RemoteUrl)) { Error = "Enter the remote repository URL."; return null; }
            return GitProviderRegistry.Normalize(RemoteUrl);
        }
        var provider = GitProviderRegistry.For(NormalizeAccount(AccountUrl));
        if (provider is null || SelectedOwner is null) { Error = "Pick an account / project, or paste an existing URL."; return null; }
        if (string.IsNullOrWhiteSpace(NewRepoName)) { Error = "Enter a repository name."; return null; }
        ProgressText = $"Creating {NewRepoName} on {provider.Name}…";
        var account = await GetAccountAsync();
        if (account is null) return null;
        var repo = await provider.CreateRepoAsync(account, SelectedOwner, NewRepoName.Trim(), NewRepoPrivate, _cts!.Token);
        RemoteUrl = repo.CloneUrl;
        return repo.CloneUrl;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Done(string folder)
    {
        IsDone = true;
        ProgressText = "✓ Published.";
        Published?.Invoke(this, folder);
    }

    private void ShowError(GitErrorInfo info)
    {
        ErrorInfo = info;
        Error = $"{info.Title}: {info.Explanation}";
        if (info.Url is not null) SsoAuthorizationUrl = info.Url;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/');
            var r = Path.GetFullPath(root).TrimEnd('\\', '/');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
