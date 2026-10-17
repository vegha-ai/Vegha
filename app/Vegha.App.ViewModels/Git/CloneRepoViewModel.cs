using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;

namespace Vegha.App.ViewModels.Git;

/// <summary>"Open from Git": clone a repository <b>in place</b> (keeping <c>.git</c>, so Source
/// Control works immediately), then open it as a workspace or link the collections it
/// contains. Source can be a pasted URL or the provider picker (GitHub / Azure DevOps account
/// → owner/project → repo).</summary>
public partial class CloneRepoViewModel : ObservableObject
{
    private readonly GitIntegration _git;
    private CancellationTokenSource? _cts;

    // ----- step 1: source ----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialSource), nameof(CanClone))]
    private string _url = string.Empty;

    /// <summary>Provider picker: account base URL, e.g. https://github.com or https://dev.azure.com/contoso.</summary>
    [ObservableProperty] private string _accountUrl = "https://github.com";

    public ObservableCollection<GitProviderOwner> Owners { get; } = new();
    [ObservableProperty] private GitProviderOwner? _selectedOwner;

    public ObservableCollection<GitProviderRepo> Repos { get; } = new();
    public ObservableCollection<GitProviderRepo> FilteredRepos { get; } = new();
    [ObservableProperty] private GitProviderRepo? _selectedRepo;
    [ObservableProperty] private string _repoFilter = string.Empty;
    [ObservableProperty] private bool _isBrowsing;
    [ObservableProperty] private string? _browseStatus;
    [ObservableProperty] private string? _ssoAuthorizationUrl;

    // ----- step 2: options ---------------------------------------------------

    public ObservableCollection<string> Branches { get; } = new();
    [ObservableProperty] private string _branch = string.Empty;
    [ObservableProperty] private string _parentFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(CanClone))]
    private string _folderName = string.Empty;

    /// <summary>Blobless partial clone — faster for big monorepos.</summary>
    [ObservableProperty] private bool _partialClone;

    // ----- step 3: progress / result ----------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    private bool _isCloning;

    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private GitErrorInfo? _errorInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _clonedPath;

    [ObservableProperty] private bool _isWorkspaceRepo;
    public ObservableCollection<CloneFoundCollection> FoundCollections { get; } = new();

    public bool HasResult => ClonedPath is not null;
    public string TargetPath => string.IsNullOrWhiteSpace(ParentFolder) || string.IsNullOrWhiteSpace(FolderName)
        ? string.Empty : Path.Combine(ParentFolder.Trim(), FolderName.Trim());
    public bool CanClone => !IsCloning && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(TargetPath);

    public string CredentialSource => string.IsNullOrWhiteSpace(Url) ? string.Empty : _git.Broker.DescribeSource(EffectiveUrl, null);

    /// <summary>Raised when the user finishes: open as workspace / link collections.</summary>
    public event EventHandler<CloneOutcome>? Completed;

    /// <summary>Raised when the user asks to configure credentials for the URL's host.</summary>
    public event EventHandler<string>? ConfigureCredentialsRequested;

    public CloneRepoViewModel(GitIntegration git, AppSettings? settings = null, string? initialUrl = null)
    {
        _git = git;
        ParentFolder = settings?.GitDefaultCloneFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Vegha", "repos");
        if (!string.IsNullOrWhiteSpace(initialUrl)) Url = initialUrl!;
    }

    /// <summary>Optional repo-relative folder to look in for collections / exports. Filled
    /// automatically from browser URLs like <c>…/tree/dev/Postman</c>.</summary>
    [ObservableProperty] private string _subFolder = string.Empty;

    private GitWebUrl? _webUrl;

    /// <summary>The URL actually cloned: the typed URL, or the clone URL derived from a URL
    /// copied from the browser (…/tree/&lt;branch&gt;/&lt;folder&gt;). The text box is never
    /// rewritten while the user types.</summary>
    public string EffectiveUrl => GitProviderRegistry.Normalize(_webUrl?.CloneUrl ?? Url.Trim());

    /// <summary>"Browser link detected — will clone … (branch dev, folder Postman)".</summary>
    [ObservableProperty] private string? _webUrlHint;

    partial void OnUrlChanged(string value)
    {
        _webUrl = GitProviderRegistry.ParseWebUrl(value);
        if (_webUrl is { } web)
        {
            if (!string.IsNullOrEmpty(web.Branch)) Branch = web.Branch!;
            SubFolder = web.Folder ?? string.Empty;
            WebUrlHint = $"Browser link detected — will clone {web.CloneUrl}" +
                (web.Branch is null ? "" : $", branch {web.Branch}") +
                (web.Folder is null ? "" : $", looking for collections in {web.Folder}") + ".";
        }
        else WebUrlHint = null;
        var parsed = GitRemoteUrl.TryParse(_webUrl?.CloneUrl ?? value);
        if (parsed is not null && (string.IsNullOrWhiteSpace(FolderName) || _folderNameAuto))
        {
            FolderName = parsed.RepoName;
            _folderNameAuto = true;
        }
        Branches.Clear();
    }

    private bool _folderNameAuto;

    partial void OnFolderNameChanged(string value) => _folderNameAuto = false;

    partial void OnSelectedRepoChanged(GitProviderRepo? value)
    {
        if (value is null) return;
        Url = value.CloneUrl;
        if (!string.IsNullOrEmpty(value.DefaultBranch)) Branch = value.DefaultBranch!;
        PartialClone = value.SizeKb is > 200_000; // >200 MB → suggest blobless clone
    }

    partial void OnRepoFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredRepos.Clear();
        foreach (var r in Repos.Where(r => string.IsNullOrWhiteSpace(RepoFilter)
                     || r.FullName.Contains(RepoFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            FilteredRepos.Add(r);
    }

    // ----- provider picker ---------------------------------------------------

    private async Task<GitProviderAccount?> GetAccountAsync()
    {
        var baseUrl = AccountUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://", StringComparison.Ordinal)) baseUrl = "https://" + baseUrl;
        var token = await _git.Broker.GetApiTokenAsync(baseUrl, null, _git.Credentials, CancellationToken.None);
        if (token is null)
        {
            BrowseStatus = "Not signed in for this host. Configure credentials (PAT, GitHub CLI, Azure CLI) or sign in once via Git Credential Manager, then retry.";
            return null;
        }
        return new GitProviderAccount(baseUrl, token.Value.Scheme, token.Value.Token);
    }

    [RelayCommand]
    private async Task LoadOwnersAsync()
    {
        var provider = GitProviderRegistry.For(AccountUrl.Contains("://", StringComparison.Ordinal) ? AccountUrl : "https://" + AccountUrl);
        if (provider is null) { BrowseStatus = "Browsing is available for GitHub and Azure DevOps. Paste a clone URL for other hosts."; return; }
        try
        {
            IsBrowsing = true;
            BrowseStatus = null;
            SsoAuthorizationUrl = null;
            Owners.Clear();
            Repos.Clear();
            FilteredRepos.Clear();
            var account = await GetAccountAsync();
            if (account is null) return;
            foreach (var o in await provider.ListOwnersAsync(account, CancellationToken.None)) Owners.Add(o);
            SelectedOwner = Owners.FirstOrDefault();
            if (Owners.Count == 0) BrowseStatus = provider is AzureDevOpsProvider ? "No projects found in this organization." : "No accounts found.";
        }
        catch (GitProviderException ex) { BrowseStatus = ex.Message; SsoAuthorizationUrl = ex.SsoAuthorizationUrl; }
        catch (Exception ex) { BrowseStatus = ex.Message; }
        finally { IsBrowsing = false; }
    }

    partial void OnSelectedOwnerChanged(GitProviderOwner? value)
    {
        if (value is not null) _ = LoadReposAsync();
    }

    private async Task LoadReposAsync()
    {
        var provider = GitProviderRegistry.For(AccountUrl.Contains("://", StringComparison.Ordinal) ? AccountUrl : "https://" + AccountUrl);
        if (provider is null || SelectedOwner is null) return;
        try
        {
            IsBrowsing = true;
            Repos.Clear();
            var account = await GetAccountAsync();
            if (account is null) return;
            foreach (var r in await provider.ListReposAsync(account, SelectedOwner, CancellationToken.None)) Repos.Add(r);
            ApplyFilter();
            BrowseStatus = Repos.Count == 0 ? "No repositories." : null;
        }
        catch (GitProviderException ex) { BrowseStatus = ex.Message; SsoAuthorizationUrl = ex.SsoAuthorizationUrl; }
        catch (Exception ex) { BrowseStatus = ex.Message; }
        finally { IsBrowsing = false; }
    }

    // ----- branches ----------------------------------------------------------

    [RelayCommand]
    private async Task LoadBranchesAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        try
        {
            Error = null;
            ErrorInfo = null;
            ProgressText = "Contacting remote…";
            var info = await _git.Service.LsRemoteAsync(EffectiveUrl);
            Branches.Clear();
            foreach (var b in info.Branches.OrderBy(b => b == info.DefaultBranch ? 0 : 1).ThenBy(b => b, StringComparer.OrdinalIgnoreCase))
                Branches.Add(b);
            if (string.IsNullOrEmpty(Branch)) Branch = info.DefaultBranch ?? Branches.FirstOrDefault() ?? string.Empty;
            ProgressText = $"✓ Connected — {info.Branches.Count} branch(es).";
        }
        catch (GitOperationException ex) { ShowError(ex.Error, ex.RawOutput); ProgressText = null; }
        catch (Exception ex) { Error = ex.Message; ProgressText = null; }
    }

    // ----- clone -------------------------------------------------------------

    [RelayCommand]
    private async Task CloneAsync()
    {
        if (!CanClone) return;
        var target = TargetPath;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            // Already cloned there? Reuse it instead of failing.
            if (GitRepoLocator.Locate(target) is { } existing && PathEquals(existing.RepoRoot, target))
            {
                ClonedPath = target;
                Scan(target);
                ProgressText = "Folder already contains this repository — using it.";
                return;
            }
            Error = $"{target} already exists and is not empty. Choose another folder name.";
            return;
        }

        IsCloning = true;
        Error = null;
        ErrorInfo = null;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(line => ProgressText = line);
            await _git.Service.CloneAsync(EffectiveUrl, target,
                string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim(), PartialClone, progress, _cts.Token);
            ClonedPath = target;
            Scan(target);
            ProgressText = "✓ Cloned.";
        }
        catch (OperationCanceledException) { ProgressText = "Cancelled."; }
        catch (GitOperationException ex) { ShowError(ex.Error, ex.RawOutput); }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsCloning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ConfigureCredentials()
    {
        var parsed = GitRemoteUrl.TryParse(string.IsNullOrWhiteSpace(Url) ? AccountUrl : EffectiveUrl);
        if (parsed is null) return;
        var host = parsed.Host;
        // GitHub: scope to the owner/org so a work account doesn't take over personal repos.
        if (GitHostKinds.IsGitHub(host) && parsed.Path.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } segs)
            host = $"{host}/{segs[0]}";
        if (GitHostKinds.IsAzureDevOps(host) && AzureDevOpsProvider.Organization(parsed) is { } org) host = "dev.azure.com/" + org;
        ConfigureCredentialsRequested?.Invoke(this, host);
    }

    /// <summary>Call after credentials were (re)configured: refreshes the "Sign-in:" line and
    /// clears the stale auth error so the next attempt starts clean.</summary>
    public void OnCredentialsChanged()
    {
        OnPropertyChanged(nameof(CredentialSource));
        Error = null;
        ErrorInfo = null;
        SsoAuthorizationUrl = null;
    }

    private void ShowError(GitErrorInfo info, string raw)
    {
        ErrorInfo = info;
        Error = $"{info.Title}: {info.Explanation}";
        if (info.Url is not null) SsoAuthorizationUrl = info.Url;
        ProgressText = raw.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
    }

    // ----- scan + finish -----------------------------------------------------

    /// <summary>Finds a workspace manifest at the root and collection folders (bruno.json /
    /// collection.bru, or folders of .bru files) up to four levels deep.</summary>
    public void Scan(string root)
    {
        FoundCollections.Clear();
        FoundImportables.Clear();
        IsWorkspaceRepo = WorkspaceManifestIO.Exists(root);
        // With a sparse folder, only that part of the repo is on disk — scan it.
        var scanRoot = string.IsNullOrWhiteSpace(SubFolder) ? root : Path.Combine(root, SubFolder.Trim().Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(scanRoot)) scanRoot = root;
        foreach (var dir in FindCollections(scanRoot, scanRoot == root ? 0 : 1))
        {
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (rel == ".") rel = $"{Path.GetFileName(root.TrimEnd('\\', '/'))} (repository root)";
            FoundCollections.Add(new CloneFoundCollection(dir, rel) { IsSelected = true });
        }
        foreach (var file in FindImportableFiles(scanRoot))
            FoundImportables.Add(new CloneFoundCollection(file, Path.GetRelativePath(root, file).Replace('\\', '/')) { IsSelected = true });
        OnPropertyChanged(nameof(HasImportables));
        OnPropertyChanged(nameof(HasNothing));
    }

    /// <summary>Exports from other tools (Postman collections + environments, Insomnia,
    /// OpenAPI / Swagger) that the Import wizard can convert.</summary>
    public ObservableCollection<CloneFoundCollection> FoundImportables { get; } = new();

    public bool HasImportables => FoundImportables.Count > 0;
    public bool HasNothing => !IsWorkspaceRepo && FoundCollections.Count == 0 && FoundImportables.Count == 0;

    public static IEnumerable<string> FindImportableFiles(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                         && !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();
        }
        catch { yield break; }
        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            if (name.EndsWith(".postman_collection.json", StringComparison.Ordinal)
                || name.EndsWith(".postman_environment.json", StringComparison.Ordinal)
                || name.EndsWith(".postman_globals.json", StringComparison.Ordinal)
                || (name.Contains("insomnia", StringComparison.Ordinal) && (name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".yaml", StringComparison.Ordinal)))
                || ((name.StartsWith("openapi", StringComparison.Ordinal) || name.StartsWith("swagger", StringComparison.Ordinal))
                    && (name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".yaml", StringComparison.Ordinal) || name.EndsWith(".yml", StringComparison.Ordinal))))
                yield return f;
        }
    }

    [RelayCommand]
    private void ImportFiles()
    {
        if (ClonedPath is null) return;
        var picked = FoundImportables.Where(c => c.IsSelected).Select(c => c.Path).ToList();
        if (picked.Count == 0) { Error = "Select at least one file."; return; }
        Completed?.Invoke(this, new CloneOutcome(ClonedPath, CloneOutcomeKind.ImportFiles, picked));
    }

    public static IEnumerable<string> FindCollections(string dir, int depth)
    {
        if (depth > 4) yield break;
        var name = Path.GetFileName(dir);
        if (depth > 0 && (name.StartsWith('.') || name is "node_modules" or "bin" or "obj")) yield break;
        bool isCollection;
        try
        {
            isCollection = File.Exists(Path.Combine(dir, "bruno.json")) || File.Exists(Path.Combine(dir, "collection.bru"))
                || (depth > 0 && Directory.EnumerateFiles(dir, "*.bru").Any() && !File.Exists(Path.Combine(dir, "folder.bru")));
        }
        catch { yield break; }
        if (isCollection) { yield return dir; yield break; }
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir).ToList(); }
        catch { yield break; }
        foreach (var sub in subs)
            foreach (var c in FindCollections(sub, depth + 1))
                yield return c;
    }

    [RelayCommand]
    private void OpenAsWorkspace()
    {
        if (ClonedPath is null) return;
        Completed?.Invoke(this, new CloneOutcome(ClonedPath, CloneOutcomeKind.OpenAsWorkspace, Array.Empty<string>()));
    }

    [RelayCommand]
    private void LinkCollections()
    {
        if (ClonedPath is null) return;
        var picked = FoundCollections.Where(c => c.IsSelected).Select(c => c.Path).ToList();
        if (picked.Count == 0) { Error = "Select at least one collection."; return; }
        Completed?.Invoke(this, new CloneOutcome(ClonedPath, CloneOutcomeKind.LinkCollections, picked));
    }

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

public partial class CloneFoundCollection : ObservableObject
{
    public string Path { get; }
    public string RelativePath { get; }
    [ObservableProperty] private bool _isSelected;

    public CloneFoundCollection(string path, string relativePath)
    {
        Path = path;
        RelativePath = relativePath;
    }
}

public enum CloneOutcomeKind { OpenAsWorkspace, LinkCollections, ImportFiles }

/// <summary>What the host should do after a successful clone.</summary>
public sealed record CloneOutcome(string RepoPath, CloneOutcomeKind Kind, IReadOnlyList<string> Collections);
