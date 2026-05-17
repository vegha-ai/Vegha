using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Git;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Source Control sidebar. Active repo path = the active collection root.
///
/// Every libgit2sharp call is run via <c>Task.Run</c> from inside the relay commands so the
/// UI thread stays responsive; the underlying <see cref="GitService"/> is intentionally sync
/// because libgit2sharp itself has no async API.
/// </summary>
public partial class GitViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly CollectionsViewModel _collections;
    private readonly ILogger<GitViewModel> _logger;
    private GitRepoWatcher? _watcher;

    // ----- core state -------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepoActive), nameof(IsNonRepoFolderActive), nameof(NoFolderActive))]
    private string? _repoPath;

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

    /// <summary>True when the most-recent network operation failed — drives the red
    /// inline footer banner so push/pull errors are impossible to miss.</summary>
    [ObservableProperty]
    private bool _lastOpFailed;

    /// <summary>Streaming git stdout/stderr from the most-recent network op. Each line is
    /// appended live so the user can watch a push/pull complete. Cleared at the start of
    /// every op.</summary>
    [ObservableProperty]
    private string _gitOutput = string.Empty;

    /// <summary>Set true when there's output worth showing — toggles the collapsible
    /// "Git Output" panel at the bottom of the source-control sidebar.</summary>
    [ObservableProperty]
    private bool _isGitOutputVisible;

    /// <summary>True while a background git op (push/pull/fetch/etc) is running.
    /// Drives the status-bar spinner and disables conflicting commands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(FetchCommand), nameof(SyncCommand))]
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

    public bool IsRepoActive => !string.IsNullOrEmpty(RepoPath) && _git.IsRepository(RepoPath);
    public bool IsNonRepoFolderActive => !string.IsNullOrEmpty(RepoPath) && !_git.IsRepository(RepoPath);
    public bool NoFolderActive => string.IsNullOrEmpty(RepoPath);

    /// <summary>True when the working tree has any staged / unstaged / untracked / conflict
    /// change. Drives the dirty "*" indicator next to the branch name in the footer.</summary>
    public bool HasWorkingChanges =>
        StagedChanges.Count > 0 || UnstagedChanges.Count > 0 ||
        UntrackedChanges.Count > 0 || MergeChanges.Count > 0;

    /// <summary>Total number of pending changes. Drives the count badge on the Git icon in
    /// the activity rail (matches SourceGit's <c>LocalChangesCount</c>).</summary>
    public int TotalChangesCount =>
        StagedChanges.Count + UnstagedChanges.Count + UntrackedChanges.Count + MergeChanges.Count;

    public bool HasUpstream => Upstream is not null;
    public string UpstreamLabel => Upstream is { } u ? $"{u.Remote}/{u.Branch}" : "";

    /// <summary>False when neither user.name nor user.email is configured. Surfaces the
    /// inline "Set git user.name and user.email" warning in the panel.</summary>
    public bool HasGitIdentity => !string.IsNullOrEmpty(Identity.Name) && !string.IsNullOrEmpty(Identity.Email);

    // ----- events -----------------------------------------------------------

    /// <summary>Raised when the user wants to open a diff for a change. MainWindow handles
    /// this by pushing a <c>GitDiffTabViewModel</c> into <c>OpenTabsViewModel</c>.</summary>
    public event EventHandler<GitChangeRow>? OpenDiffRequested;

    /// <summary>Raised when the user wants to open the actual file (not a diff). MainWindow
    /// opens it in the system default app.</summary>
    public event EventHandler<GitChangeRow>? OpenFileRequested;

    /// <summary>Raised when the user wants to be prompted for credentials. MainWindow shows
    /// the dialog (wired to <c>GitCredentialsService.PromptFallback</c>).</summary>
    public event EventHandler? CredentialsPromptRequested;

    public GitViewModel(GitService git, CollectionsViewModel collections, ILogger<GitViewModel> logger)
    {
        _git = git;
        _collections = collections;
        _logger = logger;

        _collections.ActiveCollectionChanged += (_, _) => UpdateRepoPath();
        UpdateRepoPath();
    }

    private void UpdateRepoPath()
    {
        // Git is per-collection: the repo path is the active collection root.
        RepoPath = _collections.ActiveCollection?.SourcePath;
        OnPropertyChanged(nameof(IsRepoActive));
        OnPropertyChanged(nameof(IsNonRepoFolderActive));
        OnPropertyChanged(nameof(NoFolderActive));

        // Replace the working-tree watcher when the active repo changes. The watcher fires
        // a debounced event for any external git op (terminal checkout, sibling editor stage,
        // CI rebase) so the panel stays in sync without manual refresh.
        _watcher?.Dispose();
        _watcher = null;
        if (IsRepoActive && !string.IsNullOrEmpty(RepoPath))
        {
            try
            {
                _watcher = new GitRepoWatcher(RepoPath);
                _watcher.RepositoryChanged += (_, _) =>
                {
                    // Marshal back to the UI thread — FileSystemWatcher fires on a worker.
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshAsync());
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start GitRepoWatcher for {Path}", RepoPath);
            }
            _ = RefreshAsync();
        }
        else
        {
            // Non-repo (or no folder) selected: drop the previous collection's change
            // rows so the activity-rail badge and the panel don't keep showing them.
            ClearWorkingState();
        }
    }

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
        OnPropertyChanged(nameof(HasWorkingChanges));
        OnPropertyChanged(nameof(TotalChangesCount));
    }

    // ----- refresh ----------------------------------------------------------

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var path = RepoPath;
        try
        {
            var snapshot = await Task.Run(() => SnapshotRepository(path)).ConfigureAwait(true);
            ApplySnapshot(snapshot);
            StatusMessage = $"On {CurrentBranch}. {StagedChanges.Count} staged · {UnstagedChanges.Count + UntrackedChanges.Count} changes" +
                (MergeChanges.Count > 0 ? $" · {MergeChanges.Count} conflicts" : "") + ".";
            OnPropertyChanged(nameof(HasWorkingChanges));
            OnPropertyChanged(nameof(TotalChangesCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git refresh failed");
            StatusMessage = $"Git: {ex.Message}";
        }
    }

    /// <summary>Synchronous adapter for legacy callers (e.g. activity-rail switch).</summary>
    public void Refresh() => _ = RefreshAsync();

    private RepoSnapshot SnapshotRepository(string path)
    {
        var current = _git.CurrentBranch(path);
        var branches = _git.Branches(path);
        var remoteBranches = _git.RemoteBranches(path);
        var changes = _git.Status(path);
        var commits = _git.RecentCommits(path, 30);
        var stashes = _git.StashList(path);
        var remotes = _git.Remotes(path);
        var identity = _git.GetIdentity(path);
        var upstream = _git.GetUpstream(path, current);
        var (ahead, behind) = _git.AheadBehind(path, current);

        return new RepoSnapshot(current, branches, remoteBranches, changes, commits, stashes, remotes, identity, upstream, ahead, behind);
    }

    private void ApplySnapshot(RepoSnapshot s)
    {
        CurrentBranch = s.Branch;
        Identity = s.Identity;
        Upstream = s.Upstream;
        AheadCount = s.Ahead;
        BehindCount = s.Behind;

        ReplaceAll(Branches, s.Branches);
        ReplaceAll(RemoteBranches, s.RemoteBranches);

        StagedChanges.Clear();
        UnstagedChanges.Clear();
        UntrackedChanges.Clear();
        MergeChanges.Clear();
        foreach (var change in s.Changes)
        {
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

    [RelayCommand]
    private void Init()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            _git.Init(RepoPath);
            // Seed .gitignore with the per-collection secrets directory before the first
            // commit so credentials never land in history. Idempotent — appends if the
            // file already exists.
            WorkspaceBootstrapper.EnsureCollectionGitIgnore(RepoPath);
            UpdateRepoPath();
            StatusMessage = "Initialized repository.";
        }
        catch (Exception ex) { _logger.LogError(ex, "Git init failed"); StatusMessage = $"Init failed: {ex.Message}"; }
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

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.StageAll(p)); await RefreshAsync(); }
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

    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.DiscardAll(p)); await RefreshAsync(); }
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
            await Task.Run(() =>
            {
                var sig = _git.GetSignature(p);
                _git.Commit(p, msg,
                    sig?.Name ?? "Vegha user",
                    sig?.Email ?? "user@vegha.local");
            });
            CommitMessage = string.Empty;
            StatusMessage = "Committed.";
            await RefreshAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Git commit failed"); StatusMessage = $"Commit failed: {ex.Message}"; }
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
        try { await Task.Run(() => _git.Checkout(p, branchName)); StatusMessage = $"Checked out {branchName}."; await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = $"Checkout failed: {ex.Message}"; }
    }

    // ----- network ----------------------------------------------------------

    private bool CanNetwork() => IsRepoActive && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task FetchAsync() =>
        await RunNetworkOpAsync("Fetching", "Fetched", (p, prog, ct) => _git.FetchAsync(p, "origin", prog, ct));

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task PullAsync() =>
        await RunNetworkOpAsync("Pulling", "Pulled", (p, prog, ct) => _git.PullAsync(p, "origin", prog, ct));

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task PushAsync() =>
        await RunNetworkOpAsync("Pushing", "Pushed",
            (p, prog, ct) => _git.PushAsync(p, "origin", branch: null, setUpstream: !HasUpstream, progress: prog, ct: ct));

    [RelayCommand(CanExecute = nameof(CanNetwork))]
    private async Task SyncAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        BeginOp("Syncing");
        try
        {
            var progress = new Progress<string>(AppendOutput);
            await _git.FetchAsync(p, "origin", progress);
            if (BehindCount > 0) await _git.PullAsync(p, "origin", progress);
            if (AheadCount > 0) await _git.PushAsync(p, "origin", branch: null, setUpstream: !HasUpstream, progress: progress);
            EndOp(success: true, doneMessage: "Synced.");
            await RefreshAsync();
        }
        catch (Exception ex) { EndOp(success: false, doneMessage: $"Sync failed: {ex.Message}"); }
    }

    /// <summary>Common wrapper for fetch / pull / push so they share progress streaming,
    /// inline output, status reporting, and the IsBusy lifecycle.</summary>
    private async Task RunNetworkOpAsync(string runningVerb, string doneVerb,
        Func<string, IProgress<string>, CancellationToken, Task> op)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        BeginOp(runningVerb);
        try
        {
            var progress = new Progress<string>(AppendOutput);
            await op(p, progress, CancellationToken.None);
            EndOp(success: true, doneMessage: $"{doneVerb}.");
            await RefreshAsync();
        }
        catch (Exception ex) { EndOp(success: false, doneMessage: $"{runningVerb} failed: {ex.Message}"); }
    }

    private void BeginOp(string runningVerb)
    {
        IsBusy = true;
        LastOpFailed = false;
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
    }

    private void AppendOutput(string line)
    {
        // Marshal to UI thread since Progress<T> may fire on a worker.
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

    /// <summary>Stashes current changes. <paramref name="message"/> is the optional stash
    /// name the user typed in the Stash dialog; empty/null produces an unnamed stash
    /// (matches <c>git stash</c> with no <c>-m</c>).</summary>
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
            File.AppendAllText(ignorePath, row.Path + Environment.NewLine);
            _ = RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $".gitignore update failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void RequestCredentialsPrompt() => CredentialsPromptRequested?.Invoke(this, EventArgs.Empty);

    // ----- legacy selection-driven diff (kept for sidebar fallback) ---------

    partial void OnSelectedChangeChanged(GitChangeRow? value)
    {
        if (value is null || string.IsNullOrEmpty(RepoPath)) { DiffText = string.Empty; return; }
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
        int Behind);

    // ----- remotes ----------------------------------------------------------

    [RelayCommand]
    public async Task AddRemoteAsync((string name, string url) args)
    {
        if (!IsRepoActive || string.IsNullOrEmpty(RepoPath)) return;
        var p = RepoPath;
        try { await Task.Run(() => _git.AddRemote(p, args.name, args.url)); await RefreshAsync(); }
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
        try { await Task.Run(() => _git.UpdateRemoteUrl(p, args.name, args.url)); await RefreshAsync(); }
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
