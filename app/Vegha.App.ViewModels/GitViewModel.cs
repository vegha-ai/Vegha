using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Source Control sidebar. The repository is <b>discovered</b> from the active
/// collection folder (walking up like git does), so a collection inside a cloned workspace or
/// monorepo works out of the box. <see cref="RepoPath"/> is the repo root; change rows carry
/// repo-relative paths; <see cref="ShowEntireRepo"/> toggles between "this collection's
/// changes" and "everything in the repo".
///
/// Every libgit2sharp call is run via <c>Task.Run</c> from inside the relay commands so the
/// UI thread stays responsive; network operations go through the git CLI.
/// </summary>
public partial class GitViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly CollectionsViewModel _collections;
    private readonly ILogger<GitViewModel> _logger;
    private readonly AppSettingsStore? _settings;
    private GitRepoWatcher? _watcher;
    private CancellationTokenSource? _opCts;
    private GitRepoContext? _context;

    // ----- core state -------------------------------------------------------

    /// <summary>Repository root (git toplevel) enclosing the active collection, or the
    /// collection folder itself when it isn't inside any repository.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepoActive), nameof(IsNonRepoFolderActive), nameof(NoFolderActive))]
    private string? _repoPath;

    /// <summary>Active collection folder.</summary>
    [ObservableProperty]
    private string? _collectionPath;

    /// <summary>Repo-relative path of the collection ("" when the collection is the repo root).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectionInSubfolder), nameof(ScopeLabel))]
    private string _collectionPrefix = string.Empty;

    /// <summary>When the collection lives in a subfolder of the repo: false = show only the
    /// collection's changes, true = show the whole repository's changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeLabel))]
    private bool _showEntireRepo;

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    public ObservableCollection<string> Branches { get; } = new();
    public ObservableCollection<string> RemoteBranches { get; } = new();
    public ObservableCollection<GitChangeRow> StagedChanges { get; } = new();
    public ObservableCollection<GitChangeRow> UnstagedChanges { get; } = new();
    public ObservableCollection<GitChangeRow> UntrackedChanges { get; } = new();
    public ObservableCollection<GitChangeRow> MergeChanges { get; } = new();
    public ObservableCollection<GitCommitRow> RecentCommits { get; } = new();
    public ObservableCollection<GitStashRow> Stashes { get; } = new();
    public ObservableCollection<GitRemoteRow> Remotes { get; } = new();

    [ObservableProperty]
    private GitChangeRow? _selectedChange;

    [ObservableProperty]
    private string _diffText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>True when the most-recent operation failed — drives the red inline footer.</summary>
    [ObservableProperty]
    private bool _lastOpFailed;

    /// <summary>Classified error from the last failed operation — drives the actionable banner
    /// (title, explanation, fix buttons).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private GitErrorInfo? _lastError;

    public bool HasError => LastError is not null;

    /// <summary>Streaming git stdout/stderr from the most-recent network op.</summary>
    [ObservableProperty]
    private string _gitOutput = string.Empty;

    [ObservableProperty]
    private bool _isGitOutputVisible;

    /// <summary>Latest progress frame ("Receiving objects: 42% (420/1000)").</summary>
    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(FetchCommand), nameof(SyncCommand), nameof(CancelOperationCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _aheadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpstream), nameof(UpstreamLabel))]
    private (string Remote, string Branch)? _upstream;

    [ObservableProperty]
    private int _behindCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitIdentity))]
    private (string? Name, string? Email) _identity;

    /// <summary>A rebase / merge / cherry-pick paused for conflict resolution.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationInProgress), nameof(OperationInProgressLabel), nameof(CanSkipOperation))]
    private GitRepoOperation _operationInProgress;

    [ObservableProperty]
    private bool _isDetachedHead;

    public bool IsRepoActive => _context is not null;
    public bool IsNonRepoFolderActive => !string.IsNullOrEmpty(CollectionPath) && _context is null && _unsupportedReason is null;

    private string? _unsupportedReason;
    public bool NoFolderActive => string.IsNullOrEmpty(CollectionPath);
    public bool IsCollectionInSubfolder => !string.IsNullOrEmpty(CollectionPrefix);
    public bool HasRemote => Remotes.Count > 0;
    public bool HasOperationInProgress => OperationInProgress != GitRepoOperation.None;
    public bool CanSkipOperation => OperationInProgress is GitRepoOperation.Rebase or GitRepoOperation.CherryPick;

    public string OperationInProgressLabel => OperationInProgress switch
    {
        GitRepoOperation.Rebase => "Rebase in progress — resolve conflicts, stage the files, then Continue.",
        GitRepoOperation.Merge => "Merge in progress — resolve conflicts, stage the files, then Continue.",
        GitRepoOperation.CherryPick => "Cherry-pick in progress.",
        GitRepoOperation.Revert => "Revert in progress.",
        _ => string.Empty,
    };

    /// <summary>"Showing: collections/orders (in repo my-workspace)".</summary>
    public string ScopeLabel => string.IsNullOrEmpty(CollectionPrefix)
        ? string.Empty
        : ShowEntireRepo ? $"Showing all changes in {RepoName}" : $"Showing changes in {CollectionPrefix}";

    public string RepoName => string.IsNullOrEmpty(RepoPath) ? string.Empty : Path.GetFileName(RepoPath.TrimEnd('\\', '/'));

    public bool HasWorkingChanges =>
        StagedChanges.Count > 0 || UnstagedChanges.Count > 0 ||
        UntrackedChanges.Count > 0 || MergeChanges.Count > 0;

    public int TotalChangesCount =>
        StagedChanges.Count + UnstagedChanges.Count + UntrackedChanges.Count + MergeChanges.Count;

    public bool HasUpstream => Upstream is not null;
    public string UpstreamLabel => Upstream is { } u ? $"{u.Remote}/{u.Branch}" : "";

    public bool HasGitIdentity => !string.IsNullOrEmpty(Identity.Name) && !string.IsNullOrEmpty(Identity.Email);

    /// <summary>True when git itself is missing — the panel shows the install prompt.</summary>
    public bool IsGitMissing => !_git.Runner.IsAvailable;

    public GitService Service => _git;

    // ----- events -----------------------------------------------------------

    public event EventHandler<GitChangeRow>? OpenDiffRequested;
    public event EventHandler<GitChangeRow>? OpenFileRequested;

    /// <summary>Raised when the user clicks a fix button on the error banner (or an action the
    /// VM can't complete by itself — publish, add remote, sign in, trust host…). MainWindow
    /// shows the matching dialog.</summary>
    public event EventHandler<GitActionRequest>? ActionRequested;

    /// <summary>Raised before committing when staged changes contain secret-looking values.
    /// The handler returns true to commit anyway.</summary>
    public Func<IReadOnlyList<GitSecretFinding>, Task<bool>>? ConfirmSecretsAsync { get; set; }

    /// <summary>Raised when a push needs the user to pick between several remotes. Returns the
    /// chosen remote name or null to cancel.</summary>
    public Func<IReadOnlyList<string>, Task<string?>>? ChooseRemoteAsync { get; set; }

    public GitViewModel(GitService git, CollectionsViewModel collections, ILogger<GitViewModel> logger, AppSettingsStore? settings = null)
    {
        _git = git;
        _collections = collections;
        _logger = logger;
        _settings = settings;

        _collections.ActiveCollectionChanged += (_, _) => UpdateRepoPath();
        UpdateRepoPath();
    }

    /// <summary>Re-resolves the repository (after init / clone / publish or settings change).</summary>
    public void Reload() => UpdateRepoPath();

    private void UpdateRepoPath()
    {
        var collection = _collections.ActiveCollection?.SourcePath;
        CollectionPath = collection;
        var located = GitRepoLocator.Locate(collection);
        // A repo Vegha can't manage (e.g. sparse checkout) is reported, never treated as
        // "not a repo" — that would offer to publish / re-init an existing repository.
        _unsupportedReason = located?.UnsupportedReason;
        _context = located is { IsSupported: true } ? located : null;
        CollectionPrefix = _context?.CollectionPrefix ?? string.Empty;
        RepoPath = located?.RepoRoot ?? collection;
        LastOpFailed = _unsupportedReason is not null;
        LastError = _unsupportedReason is null ? null
            : new GitErrorInfo(GitErrorKind.Unknown, "Repository not supported", _unsupportedReason, Array.Empty<GitErrorAction>());
        OnPropertyChanged(nameof(IsRepoActive));
        OnPropertyChanged(nameof(IsNonRepoFolderActive));
        OnPropertyChanged(nameof(NoFolderActive));
        OnPropertyChanged(nameof(RepoName));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(IsGitMissing));
        RepoRemoteLabel = _context is null ? string.Empty : DescribeRemote(_context.RepoRoot);

        // Replace the working-tree watcher when the active repo changes. The watcher fires a
        // debounced event for any external git op (terminal checkout, IDE stage, CI rebase).
        _watcher?.Dispose();
        _watcher = null;
        if (_context is not null)
        {
            try
            {
                _watcher = new GitRepoWatcher(_context.RepoRoot, _context.GitDir);
                _watcher.RepositoryChanged += (_, _) =>
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshAsync());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start GitRepoWatcher for {Path}", _context.RepoRoot);
            }
            _ = RefreshAsync();
        }
        else
        {
            ClearWorkingState();
        }
    }

    /// <summary>Read-only "where am I committing" line under the panel header:
    /// "origin · dev.azure.com/org/project/_git/repo". Source Control always follows the
    /// active collection — pick a different collection to work in a different repo.</summary>
    [ObservableProperty]
    private string _repoRemoteLabel = string.Empty;

    /// <summary>"origin · dev.azure.com/org/project/_git/repo" (origin preferred, else the
    /// first remote).</summary>
    private string DescribeRemote(string repoRoot)
    {
        try
        {
            var remotes = _git.Remotes(repoRoot);
            var r = remotes.FirstOrDefault(x => x.Name == "origin") ?? remotes.FirstOrDefault();
            if (r is null) return "No remote";
            var url = r.Url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http", StringComparison.Ordinal))
                url = u.Host + u.AbsolutePath; // drops scheme and any "user@"
            return $"{r.Name} · {url}" + (remotes.Count > 1 ? $"  (+{remotes.Count - 1})" : "");
        }
        catch { return string.Empty; }
    }

    partial void OnShowEntireRepoChanged(bool value) => _ = RefreshAsync();

    private void ClearWorkingState()
    {
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        UntrackedChanges.Clear();
        MergeChanges.Clear();
        RecentCommits.Clear();
        Stashes.Clear();
        Remotes.Clear();
        Branches.Clear();
        RemoteBranches.Clear();
        CurrentBranch = string.Empty;
        DiffText = string.Empty;
        AheadCount = 0;
        BehindCount = 0;
        Upstream = null;
        OperationInProgress = GitRepoOperation.None;
        OnPropertyChanged(nameof(HasWorkingChanges));
        OnPropertyChanged(nameof(TotalChangesCount));
        OnPropertyChanged(nameof(HasRemote));
    }

    // ----- refresh ----------------------------------------------------------

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_context is null || string.IsNullOrEmpty(RepoPath)) return;
        var path = RepoPath;
        try
        {
            var snapshot = await Task.Run(() => SnapshotRepository(path)).ConfigureAwait(true);
            ApplySnapshot(snapshot);
            RepoRemoteLabel = DescribeRemote(path);
            if (!IsBusy && !LastOpFailed && DateTime.UtcNow >= _statusHoldUntil)
                StatusMessage = (IsDetachedHead ? "Detached HEAD" : $"On {CurrentBranch}") +
                    $". {StagedChanges.Count} staged · {UnstagedChanges.Count + UntrackedChanges.Count} changes" +
                    (MergeChanges.Count > 0 ? $" · {MergeChanges.Count} conflicts" : "") + ".";
            OnPropertyChanged(nameof(HasWorkingChanges));
            OnPropertyChanged(nameof(TotalChangesCount));
            OnPropertyChanged(nameof(HasRemote));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git refresh failed");
            StatusMessage = $"Git: {ex.Message}";
        }
    }

    public void Refresh() => _ = RefreshAsync();

    private RepoSnapshot SnapshotRepository(string path)
    {
        var current = _git.CurrentBranch(path);
        var branches = _git.Branches(path);
        var remoteBranches = _git.RemoteBranches(path);
        var changes = _git.Status(path);
        var commits = _git.IsUnborn(path) ? Array.Empty<GitCommitSummary>() : _git.RecentCommits(path, 30);
        var stashes = _git.StashList(path);
        var remotes = _git.Remotes(path);
        var identity = _git.GetIdentity(path);
        var upstream = _git.GetUpstream(path, current);
        var (ahead, behind) = _git.AheadBehind(path, current);
        var op = _git.CurrentOperation(path);
        var detached = _git.IsDetachedHead(path);

        return new RepoSnapshot(current, branches, remoteBranches, changes, commits, stashes, remotes, identity, upstream, ahead, behind, op, detached);
    }

    private void ApplySnapshot(RepoSnapshot s)
    {
        CurrentBranch = s.Branch;
        Identity = s.Identity;
        Upstream = s.Upstream;
        AheadCount = s.Ahead;
        BehindCount = s.Behind;
        OperationInProgress = s.Operation;
        IsDetachedHead = s.Detached;

        ReplaceAll(Branches, s.Branches);
        ReplaceAll(RemoteBranches, s.RemoteBranches);

        StagedChanges.Clear();
        UnstagedChanges.Clear();
        UntrackedChanges.Clear();
        MergeChanges.Clear();
        var filterPrefix = ShowEntireRepo ? string.Empty : CollectionPrefix;
        foreach (var change in s.Changes)
        {
            // Conflicts are always shown — they block the whole repo regardless of scope.
            if (change.Kind != GitChangeKind.Conflict && !GitRepoLocator.IsUnderPrefix(change.Path, filterPrefix)) continue;
            var row = new GitChangeRow(change.Path, change.Kind, change.IsStaged);
            if (change.Kind == GitChangeKind.Conflict) MergeChanges.Add(row);
            else if (change.IsStaged) StagedChanges.Add(row);
            else if (change.Kind == GitChangeKind.Added) UntrackedChanges.Add(row);
            else UnstagedChanges.Add(row);
        }

        RecentCommits.Clear();
        foreach (var c in s.RecentCommits)
            RecentCommits.Add(new GitCommitRow(c.Sha, c.MessageShort, c.AuthorName, c.When.ToString("yyyy-MM-dd HH:mm")));

        Stashes.Clear();
        foreach (var st in s.Stashes)
            Stashes.Add(new GitStashRow(st.Index, st.Sha, st.Message, st.When.ToString("yyyy-MM-dd HH:mm")));

        Remotes.Clear();
        foreach (var r in s.Remotes)
            Remotes.Add(new GitRemoteRow(r.Name, r.Url));
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    // ----- repo lifecycle ---------------------------------------------------

    /// <summary>"Initialize / Publish" from the not-a-repo empty state. Opens the Publish
    /// wizard (scope choice, .gitignore, remote) rather than blindly running git init.</summary>
    [RelayCommand]
    private void Init() => RequestAction(GitErrorAction.PublishRepository, null);

    /// <summary>Plain local <c>git init</c> at <paramref name="folder"/> (used by the Publish
    /// wizard). Refuses when the folder is already inside a repository — that's how nested
    /// repos without remotes used to get created.</summary>
    public async Task<bool> InitAtAsync(string folder, string? initialBranch = null)
    {
        if (GitRepoLocator.Locate(folder) is { } existing)
        {
            StatusMessage = $"Already inside repository {existing.RepoRoot}.";
            return false;
        }
        try
        {
            await _git.InitAsync(folder, initialBranch);
            WorkspaceBootstrapper.EnsureRepoGitIgnore(folder);
            if (!string.IsNullOrEmpty(CollectionPath) && !PathEquals(CollectionPath, folder))
                WorkspaceBootstrapper.EnsureCollectionGitIgnore(CollectionPath);
            UpdateRepoPath();
            StatusMessage = "Initialized repository.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git init failed");
            ShowFailure("init", ex);
            return false;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ----- staging / discard ------------------------------------------------

    [RelayCommand]
    private async Task StageAsync(GitChangeRow? row)
    {
        if (row is null || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.Stage(p, row.Path)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Stage failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task UnstageAsync(GitChangeRow? row)
    {
        if (row is null || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.Unstage(p, row.Path)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Unstage failed: {ex.Message}"; }
    }

    /// <summary>Stages every change currently listed (respects the collection/repo scope).</summary>
    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            if (ShowEntireRepo || string.IsNullOrEmpty(CollectionPrefix))
                await Task.Run(() => _git.StageAll(p));
            else
            {
                var paths = UnstagedChanges.Concat(UntrackedChanges).Concat(MergeChanges).Select(c => c.Path).ToArray();
                if (paths.Length > 0) await Task.Run(() => _git.Stage(p, paths));
            }
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Stage all failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            var snapshot = StagedChanges.Select(c => c.Path).ToArray();
            await Task.Run(() => _git.Unstage(p, snapshot));
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Unstage all failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DiscardFileAsync(GitChangeRow? row)
    {
        if (row is null || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            if (row.Kind == GitChangeKind.Added && !row.IsStaged)
            {
                // Untracked — delete from disk. Recycle bin on Windows so it's recoverable.
                var abs = Path.Combine(p, row.Path);
                if (File.Exists(abs))
                {
                    if (OperatingSystem.IsWindows())
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(abs,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        File.Delete(abs);
                }
            }
            else
            {
                await Task.Run(() => _git.DiscardFile(p, row.Path));
            }
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Discard failed: {ex.Message}"; }
    }

    /// <summary>Discards every listed change. Scoped to the collection unless the panel shows
    /// the entire repository — never silently reverts files outside what the user sees.</summary>
    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            if (ShowEntireRepo || string.IsNullOrEmpty(CollectionPrefix))
                await Task.Run(() => _git.DiscardAll(p));
            else
                foreach (var row in StagedChanges.Concat(UnstagedChanges).ToList())
                {
                    var r = row;
                    if (r.Kind == GitChangeKind.Added) await Task.Run(() => _git.Unstage(p, r.Path));
                    else await Task.Run(() => _git.DiscardFile(p, r.Path));
                }
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Discard all failed: {ex.Message}"; }
    }

    // ----- commit -----------------------------------------------------------

    private bool CanCommit() => !string.IsNullOrWhiteSpace(CommitMessage) && IsRepoActive && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        var msg = CommitMessage;
        try
        {
            IsBusy = true;
            LastError = null;
            LastOpFailed = false;

            // Nothing staged → commit everything the panel lists (VS Code "smart commit"),
            // scoped to the collection unless "Entire repo" is on. Without this the commit hits
            // an empty index and libgit2 throws "No changes; nothing to commit".
            var autoStaged = 0;
            if (StagedChanges.Count == 0)
            {
                var paths = UnstagedChanges.Concat(UntrackedChanges).Select(c => c.Path).Distinct().ToArray();
                if (paths.Length == 0)
                {
                    StatusMessage = MergeChanges.Count > 0 ? "Resolve the conflicts first, then commit." : "Nothing to commit.";
                    return;
                }
                await Task.Run(() => _git.Stage(p, paths));
                autoStaged = paths.Length;
            }

            if (_settings?.Load().GitScanSecretsOnCommit != false && ConfirmSecretsAsync is not null)
            {
                var findings = await Task.Run(() => GitSecretScanner.ScanStaged(_git, p));
                if (findings.Count > 0 && !await ConfirmSecretsAsync(findings))
                {
                    StatusMessage = "Commit cancelled — review the flagged values.";
                    await RefreshAsync();
                    return;
                }
            }

            await Task.Run(() =>
            {
                var sig = _git.GetSignature(p);
                _git.Commit(p, msg,
                    sig?.Name ?? "Vegha user",
                    sig?.Email ?? "user@vegha.local");
            });
            CommitMessage = string.Empty;
            IsBusy = false;
            await RefreshAsync();
            StatusMessage = (autoStaged > 0 ? $"Committed {autoStaged} change{(autoStaged == 1 ? "" : "s")}." : "Committed.")
                + (HasRemote ? " Push to share it." : "");
            HoldStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git commit failed");
            // Surface it in the banner — a footer line alone was easy to miss.
            LastError = new GitErrorInfo(GitErrorKind.Unknown, "Commit failed", ex.Message, Array.Empty<GitErrorAction>());
            StatusMessage = "Commit failed.";
            LastOpFailed = true;
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CommitAmendAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        var msg = string.IsNullOrWhiteSpace(CommitMessage) ? null : CommitMessage;
        try
        {
            IsBusy = true;
            await Task.Run(() => _git.AmendCommit(p, msg));
            CommitMessage = string.Empty;
            StatusMessage = "Amended commit.";
            await RefreshAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Git amend failed"); StatusMessage = $"Amend failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task CheckoutAsync(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName) || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { if (_git.Runner.IsAvailable) await _git.CheckoutAsync(p, branchName); else await Task.Run(() => _git.Checkout(p, branchName)); StatusMessage = $"Checked out {branchName}."; await RefreshAsync(); }
        catch (Exception ex) { ShowFailure("checkout", ex); }
    }

    // ----- network ----------------------------------------------------------

    private bool CanNetwork() => IsRepoActive && !IsBusy;

    private GitPullMode PullMode => (_settings?.Load().GitPullMode ?? "rebase") switch
    {
        "merge" => GitPullMode.Merge,
        "ff-only" => GitPullMode.FastForwardOnly,
        _ => GitPullMode.Rebase,
    };

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private Task FetchAsync() =>
        RunNetworkOpAsync("fetch", "Fetching", "Fetched", (p, prog, ct) => _git.FetchAsync(p, null, prog, ct));

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private Task PullAsync() =>
        RunNetworkOpAsync("pull", "Pulling", "Pulled", (p, prog, ct) => _git.PullAsync(p, PullMode, prog, ct));

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task PushAsync()
    {
        var remote = await PickPushRemoteAsync();
        if (remote == string.Empty) return; // cancelled
        await RunNetworkOpAsync("push", "Pushing", "Pushed",
            (p, prog, ct) => _git.PushAsync(p, remote, branch: null, setUpstream: !HasUpstream, progress: prog, ct: ct));
    }

    /// <summary>Asks which remote to push to when several exist and none is implied.
    /// Returns null for "let git decide", "" for cancelled.</summary>
    private async Task<string?> PickPushRemoteAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || ChooseRemoteAsync is null) return null;
        var p = RepoPath;
        var branch = CurrentBranch;
        var needs = await Task.Run(() => { try { return _git.NeedsRemoteChoice(p, branch); } catch { return false; } });
        if (!needs) return null;
        var choice = await ChooseRemoteAsync(Remotes.Select(r => r.Name).ToList());
        return choice ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task SyncAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var remote = await PickPushRemoteAsync();
        if (remote == string.Empty) return;
        await RunNetworkOpAsync("sync", "Syncing", "Synced", async (p, prog, ct) =>
        {
            await _git.FetchAsync(p, null, prog, ct);
            // Ahead/behind are stale until after the fetch — recompute.
            var branch = _git.CurrentBranch(p);
            var (ahead, behind) = _git.AheadBehind(p, branch);
            var hasUpstream = _git.GetUpstream(p, branch) is not null;
            if (behind > 0) await _git.PullAsync(p, PullMode, prog, ct);
            if (ahead > 0 || !hasUpstream) await _git.PushAsync(p, remote, branch: null, setUpstream: !hasUpstream, progress: prog, ct: ct);
        });
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelOperation() => _opCts?.Cancel();

    /// <summary>Common wrapper for network ops: progress streaming, cancellation, classified
    /// errors, IsBusy lifecycle.</summary>
    private async Task RunNetworkOpAsync(string op, string runningVerb, string doneVerb,
        Func<string, IProgress<string>, CancellationToken, Task> action)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        BeginOp(runningVerb);
        _opCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(OnProgressLine);
            await action(p, progress, _opCts.Token);
            IsBusy = false;
            await RefreshAsync();
            // After the refresh so the confirmation isn't replaced by the generic status line.
            EndOp(success: true, doneMessage: $"{doneVerb}.");
        }
        catch (OperationCanceledException)
        {
            EndOp(success: false, doneMessage: $"{runningVerb} cancelled.");
            LastError = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git {Op} failed", op);
            ShowFailure(op, ex);
            await RefreshAsync();
        }
        finally
        {
            _opCts?.Dispose();
            _opCts = null;
            ProgressText = null;
        }
    }

    /// <summary>Runs an arbitrary network action (clone / publish flows reuse the panel's
    /// progress + error UX).</summary>
    public Task RunExternalOpAsync(string op, string runningVerb, string doneVerb, Func<IProgress<string>, CancellationToken, Task> action) =>
        RunNetworkOpAsync(op, runningVerb, doneVerb, (_, prog, ct) => action(prog, ct));

    private void ShowFailure(string op, Exception ex)
    {
        var info = ex is GitOperationException g ? g.Error : GitErrorClassifier.Classify(ex.Message, op);
        IsBusy = false;
        LastOpFailed = true;
        LastError = info;
        StatusMessage = info.Title;
        if (ex is GitOperationException ge && !string.IsNullOrWhiteSpace(ge.RawOutput)) AppendOutput(ge.RawOutput.TrimEnd());
        else AppendOutput(ex.Message);
        IsGitOutputVisible = true;
    }

    private void BeginOp(string runningVerb)
    {
        IsBusy = true;
        LastOpFailed = false;
        LastError = null;
        GitOutput = string.Empty;
        IsGitOutputVisible = true;
        StatusMessage = $"{runningVerb}...";
    }

    private void EndOp(bool success, string doneMessage)
    {
        IsBusy = false;
        StatusMessage = doneMessage;
        LastOpFailed = !success;
        if (!success) AppendOutput(doneMessage);
        else HoldStatus();
    }

    private DateTime _statusHoldUntil;

    /// <summary>Keeps a confirmation ("Pushed.", "Committed 1 change.") visible for a few
    /// seconds — the file watcher refreshes right after every git op and would otherwise
    /// replace it with the generic "On main…" line immediately.</summary>
    private void HoldStatus() => _statusHoldUntil = DateTime.UtcNow.AddSeconds(6);

    private void OnProgressLine(string line)
    {
        // Progress repaint frames ("Receiving objects:  42%") update a single status line;
        // everything else goes to the transcript.
        if (LooksLikeProgress(line))
        {
            // Progress<T> posts asynchronously, so frames can land after the op finished —
            // don't let them overwrite the "Pushed." confirmation.
            if (!IsBusy) return;
            ProgressText = line;
            StatusMessage = line;
            return;
        }
        AppendOutput(line);
    }

    private static bool LooksLikeProgress(string line) =>
        line.Contains('%', StringComparison.Ordinal) &&
        (line.StartsWith("Receiving", StringComparison.Ordinal) || line.StartsWith("Resolving", StringComparison.Ordinal)
         || line.StartsWith("Counting", StringComparison.Ordinal) || line.StartsWith("Compressing", StringComparison.Ordinal)
         || line.StartsWith("Writing", StringComparison.Ordinal) || line.StartsWith("remote:", StringComparison.Ordinal)
         || line.StartsWith("Unpacking", StringComparison.Ordinal) || line.StartsWith("Updating files", StringComparison.Ordinal)
         || line.StartsWith("Enumerating", StringComparison.Ordinal));

    private void AppendOutput(string line)
    {
        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            GitOutput += string.IsNullOrEmpty(GitOutput) ? line : "\n" + line;
        else
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendOutput(line));
    }

    [RelayCommand]
    private void ClearGitOutput()
    {
        GitOutput = string.Empty;
        IsGitOutputVisible = false;
    }

    [RelayCommand]
    private void ToggleGitOutput() => IsGitOutputVisible = !IsGitOutputVisible;

    [RelayCommand]
    private void DismissError()
    {
        LastError = null;
        LastOpFailed = false;
    }

    /// <summary>Error-banner button → VM-handled action, or forwarded to MainWindow.</summary>
    [RelayCommand]
    private async Task ErrorActionAsync(GitErrorAction action)
    {
        switch (action)
        {
            case GitErrorAction.PullFirst:
                LastError = null;
                await PullAsync();
                break;
            case GitErrorAction.StashAndRetry:
                LastError = null;
                await StashAsync("Vegha: auto-stash before sync");
                break;
            case GitErrorAction.ResolveConflicts:
                LastError = null;
                break;
            default:
                RequestAction(action, LastError);
                break;
        }
    }

    private void RequestAction(GitErrorAction action, GitErrorInfo? info) =>
        ActionRequested?.Invoke(this, new GitActionRequest(action, info, RepoPath, CollectionPath, CurrentRemoteUrl()));

    /// <summary>URL of the remote the next push would use (for diagnostics / sign-in).</summary>
    public string? CurrentRemoteUrl()
    {
        if (_context is null || string.IsNullOrEmpty(RepoPath)) return null;
        try
        {
            var remote = _git.ResolveRemote(RepoPath, CurrentBranch, forPush: true);
            return remote is null ? Remotes.FirstOrDefault()?.Url : _git.RemoteUrl(RepoPath, remote, forPush: true);
        }
        catch { return null; }
    }

    // ----- in-progress rebase / merge --------------------------------------

    [RelayCommand]
    private Task ContinueOperationAsync() => ControlOperationAsync(GitOperationControl.Continue);

    [RelayCommand]
    private Task AbortOperationAsync() => ControlOperationAsync(GitOperationControl.Abort);

    [RelayCommand]
    private Task SkipOperationAsync() => ControlOperationAsync(GitOperationControl.Skip);

    private async Task ControlOperationAsync(GitOperationControl control)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            IsBusy = true;
            await _git.ControlOperationAsync(p, control);
            LastError = null;
            LastOpFailed = false;
            StatusMessage = control switch
            {
                GitOperationControl.Abort => "Aborted.",
                GitOperationControl.Skip => "Skipped commit.",
                _ => "Continued.",
            };
        }
        catch (Exception ex) { ShowFailure("rebase", ex); }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    // ----- branches ---------------------------------------------------------

    [RelayCommand]
    public async Task CreateBranchAsync(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName) || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try
        {
            await Task.Run(() =>
            {
                _git.CreateBranch(p, branchName);
                _git.Checkout(p, branchName);
            });
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Create branch failed: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task DeleteBranchAsync(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName) || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.DeleteBranch(p, branchName)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Delete branch failed: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task RenameBranchAsync((string oldName, string newName) names)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.RenameBranch(p, names.oldName, names.newName)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Rename branch failed: {ex.Message}"; }
    }

    // ----- stash ------------------------------------------------------------

    [RelayCommand]
    private async Task StashAsync(string? message)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        var msg = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        try { await Task.Run(() => _git.Stash(p, msg)); await RefreshAsync(); StatusMessage = msg is null ? "Stashed." : $"Stashed: {msg}"; }
        catch (Exception ex) { StatusMessage = $"Stash failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StashPopAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.StashPop(p)); await RefreshAsync(); StatusMessage = "Stash popped."; }
        catch (Exception ex) { StatusMessage = $"Stash pop failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StashApplyAsync(int index)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.StashApply(p, index)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Stash apply failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StashDropAsync(int index)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.StashDrop(p, index)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Stash drop failed: {ex.Message}"; }
    }

    // ----- conflict resolution ---------------------------------------------

    [RelayCommand]
    private async Task ResolveOursAsync(GitChangeRow? row)
    {
        if (row is null || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.ResolveAs(p, row.Path, ConflictSide.Ours)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Resolve failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ResolveTheirsAsync(GitChangeRow? row)
    {
        if (row is null || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.ResolveAs(p, row.Path, ConflictSide.Theirs)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Resolve failed: {ex.Message}"; }
    }

    // ----- identity ---------------------------------------------------------

    [RelayCommand]
    public async Task SetGitIdentityAsync((string name, string email) ident)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.SetIdentity(p, ident.name, ident.email)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Set identity failed: {ex.Message}"; }
    }

    // ----- open file / diff -------------------------------------------------

    [RelayCommand]
    private void OpenFile(GitChangeRow? row)
    {
        if (row is null) return;
        OpenFileRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void OpenDiff(GitChangeRow? row)
    {
        if (row is null) return;
        OpenDiffRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void AddToGitignore(GitChangeRow? row)
    {
        if (row is null || string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            var ignorePath = Path.Combine(RepoPath, ".gitignore");
            File.AppendAllText(ignorePath, row.Path.Replace('\\', '/') + Environment.NewLine);
            _ = RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $".gitignore update failed: {ex.Message}"; }
    }

    /// <summary>Opens the Publish wizard (header / empty-state "Publish…").</summary>
    [RelayCommand]
    private void Publish() => RequestAction(GitErrorAction.PublishRepository, null);

    /// <summary>Opens "Test connection" diagnostics for the current remote.</summary>
    [RelayCommand]
    private void OpenDiagnostics() => RequestAction(GitErrorAction.OpenDiagnostics, LastError);

    /// <summary>Opens credentials configuration for the current remote's host.</summary>
    [RelayCommand]
    private void ConfigureCredentials() => RequestAction(GitErrorAction.ConfigureCredentials, LastError);

    /// <summary>Starts the on-demand git download when git is missing.</summary>
    [RelayCommand]
    private void InstallGit() => RequestAction(GitErrorAction.InstallGit, null);

    // ----- legacy selection-driven diff (kept for sidebar fallback) ---------

    partial void OnSelectedChangeChanged(GitChangeRow? value)
    {
        if (value is null || string.IsNullOrEmpty(RepoPath) || _context is null) { DiffText = string.Empty; return; }
        try
        {
            DiffText = value.IsStaged ? _git.StagedDiff(RepoPath) : _git.UnstagedDiff(RepoPath);
        }
        catch (Exception ex) { DiffText = $"diff failed: {ex.Message}"; }
    }

    // ----- data carriers ----------------------------------------------------

    private sealed record RepoSnapshot(
        string Branch,
        IReadOnlyList<string> Branches,
        IReadOnlyList<string> RemoteBranches,
        IReadOnlyList<GitFileChange> Changes,
        IReadOnlyList<GitCommitSummary> RecentCommits,
        IReadOnlyList<GitStashEntry> Stashes,
        IReadOnlyList<GitRemoteEntry> Remotes,
        (string? Name, string? Email) Identity,
        (string Remote, string Branch)? Upstream,
        int Ahead,
        int Behind,
        GitRepoOperation Operation,
        bool Detached);

    // ----- remotes ----------------------------------------------------------

    [RelayCommand]
    public async Task AddRemoteAsync((string name, string url) args)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        var url = Vegha.Integrations.Git.Providers.GitProviderRegistry.Normalize(args.url);
        try { await Task.Run(() => _git.AddRemote(p, args.name, url)); LastError = null; LastOpFailed = false; await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Add remote failed: {ex.Message}"; LastOpFailed = true; }
    }

    [RelayCommand]
    public async Task RemoveRemoteAsync(string? name)
    {
        if (string.IsNullOrEmpty(name) || !IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.RemoveRemote(p, name)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Remove remote failed: {ex.Message}"; LastOpFailed = true; }
    }

    [RelayCommand]
    public async Task UpdateRemoteUrlAsync((string name, string url) args)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        var url = Vegha.Integrations.Git.Providers.GitProviderRegistry.Normalize(args.url);
        try { await Task.Run(() => _git.UpdateRemoteUrl(p, args.name, url)); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Update remote failed: {ex.Message}"; LastOpFailed = true; }
    }
}

public sealed record GitChangeRow(string Path, GitChangeKind Kind, bool IsStaged)
{
    public string KindLabel => Kind switch
    {
        GitChangeKind.Added => IsStaged ? "A" : "U",
        GitChangeKind.Modified => "M",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Conflict => "!",
        _ => "?",
    };

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

public sealed record GitCommitRow(string Sha, string Message, string Author, string When)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}

public sealed record GitStashRow(int Index, string Sha, string Message, string When)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}

public sealed record GitRemoteRow(string Name, string Url);

/// <summary>Something the Source Control panel needs the host window to do.</summary>
public sealed record GitActionRequest(GitErrorAction Action, GitErrorInfo? Error, string? RepoRoot, string? CollectionPath, string? RemoteUrl);
