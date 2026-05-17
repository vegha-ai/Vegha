namespace Vegha.Integrations.Git;

/// <summary>Watches a repo's working tree + <c>.git</c> metadata for changes and raises a
/// single debounced event. Drives the <c>GitViewModel</c> auto-refresh so external
/// <c>git</c> commands (terminal, IDE, etc.) show up in the UI without manual refresh.</summary>
public sealed class GitRepoWatcher : IDisposable
{
    private readonly FileSystemWatcher _workdir;
    private readonly FileSystemWatcher _gitDir;
    private readonly System.Threading.Timer _debounce;
    private readonly object _gate = new();
    private bool _pending;
    private bool _disposed;

    public event EventHandler? RepositoryChanged;

    /// <summary>Window in milliseconds during which multiple FS events collapse into a single
    /// <see cref="RepositoryChanged"/> emission. Short enough to feel snappy, long enough that
    /// a bulk rebase / branch checkout doesn't fire a flood.</summary>
    public int DebounceMs { get; init; } = 300;

    public GitRepoWatcher(string repoPath)
    {
        var gitFolder = Path.Combine(repoPath, ".git");

        _workdir = new FileSystemWatcher(repoPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        _workdir.Changed += OnFsEvent;
        _workdir.Created += OnFsEvent;
        _workdir.Deleted += OnFsEvent;
        _workdir.Renamed += OnFsEvent;

        _gitDir = new FileSystemWatcher(gitFolder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _gitDir.Changed += OnFsEvent;
        _gitDir.Created += OnFsEvent;

        _debounce = new System.Threading.Timer(_ => Fire(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _workdir.EnableRaisingEvents = true;
        _gitDir.EnableRaisingEvents = true;
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        // Drop noise: anything under .git/objects/ (the bulk of repo write traffic during git
        // ops) — we already watch HEAD/index/refs which capture meaningful state changes.
        if (e.FullPath.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar + "objects", StringComparison.Ordinal))
            return;
        lock (_gate)
        {
            _pending = true;
            _debounce.Change(DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    private void Fire()
    {
        bool shouldFire;
        lock (_gate)
        {
            shouldFire = _pending;
            _pending = false;
        }
        if (shouldFire) RepositoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _workdir.EnableRaisingEvents = false; _workdir.Dispose(); } catch { }
        try { _gitDir.EnableRaisingEvents = false; _gitDir.Dispose(); } catch { }
        try { _debounce.Dispose(); } catch { }
    }
}
