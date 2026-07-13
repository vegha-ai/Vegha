using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.History;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    /// <summary>Initial page size + step for "load more" / scroll-triggered paging.</summary>
    public const int PageSize = 30;

    private readonly HistoryStore _store;
    private readonly ILogger<HistoryViewModel> _logger;

    /// <summary>Tracks how many rows are currently in <see cref="Items"/> so the next page
    /// can be requested at the right offset. Reset on every <see cref="RefreshAsync"/>.</summary>
    private int _loadedCount;

    /// <summary>De-dupes selection-change firings so arrow-key scanning doesn't open the same
    /// tab repeatedly. The host activates the existing tab anyway, but we avoid the work.</summary>
    private long? _lastOpenedRowId;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    /// <summary>True when the most recent page came back full — i.e. more rows likely exist.
    /// Drives the "Load more" affordance and the scroll-trigger guard.</summary>
    [ObservableProperty] private bool _hasMore;

    /// <summary>Folder path of the workspace whose history is shown. Set by the host on startup
    /// and every workspace switch; changing it reloads the list so each workspace shows only its
    /// own requests. Null means "all workspaces" (no filter).</summary>
    [ObservableProperty] private string? _workspaceId;

    /// <summary>Free-text filter matched (case-insensitive) against method + URL. Bound to the
    /// panel's search box; edits reload the list (debounced).</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>True when the list has no rows — drives the centered empty-state message.</summary>
    [ObservableProperty] private bool _isListEmpty = true;

    /// <summary>Empty-state copy, switched between "no history yet" and "no matches" depending on
    /// whether a search filter is active.</summary>
    [ObservableProperty] private string _emptyStateText = NoHistoryText;

    /// <summary>Title/subtitle split of the empty-state copy — drives the shared icon-circle
    /// empty-state layout (same pattern as the Environments and Runner panels).</summary>
    [ObservableProperty] private string _emptyStateTitle = NoHistoryTitle;
    [ObservableProperty] private string _emptyStateSubtitle = NoHistorySubtitle;

    private const string NoHistoryTitle = "No requests yet";
    private const string NoHistorySubtitle = "Send a request and it'll show up here.";
    private const string NoMatchesTitle = "No matching requests";
    private const string NoMatchesSubtitle = "Try a different search.";
    private const string NoHistoryText = NoHistoryTitle + ".\n\n" + NoHistorySubtitle;
    private const string NoMatchesText = NoMatchesTitle + ".\n\n" + NoMatchesSubtitle;

    /// <summary>Debounces <see cref="SearchText"/> edits so each keystroke doesn't hit SQLite.</summary>
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>Currently-highlighted row in the list. Setting this fires
    /// <see cref="OpenInTabRequested"/> so the host opens (or activates) a tab.</summary>
    [ObservableProperty]
    private HistoryRow? _selectedRow;

    public ObservableCollection<HistoryRow> Items { get; } = new();

    public HistoryViewModel(
        HistoryStore store,
        ILogger<HistoryViewModel> logger)
    {
        _store = store;
        _logger = logger;
    }

    partial void OnSelectedRowChanged(HistoryRow? value)
    {
        if (value is null) return;
        if (_lastOpenedRowId == value.Id) return;
        _lastOpenedRowId = value.Id;
        _ = RaiseOpenInTabAsync(value);
    }

    /// <summary>Loads a row's stored entry + request blob and projects them into the replay
    /// payload the host uses to rebuild a tab. Shared by every "open from history" path.</summary>
    private async Task<HistoryReplayPayload> BuildPayloadAsync(HistoryRow row)
    {
        var entryTask = _store.GetByIdAsync(row.Id);
        var blobTask = _store.GetRequestBlobAsync(row.Id);
        await Task.WhenAll(entryTask, blobTask).ConfigureAwait(true);
        return new HistoryReplayPayload(
            Row: row,
            RequestBlob: blobTask.Result,
            ResponseBody: entryTask.Result?.ResponseBodyPreview);
    }

    private async Task RaiseOpenInTabAsync(HistoryRow row)
    {
        try
        {
            OpenInTabRequested?.Invoke(this, await BuildPayloadAsync(row));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open history tab for id {Id}", row.Id);
        }
    }

    /// <summary>Raised when the user selects a history entry; the host turns the payload
    /// into a tab via <c>OpenTabsViewModel.OpenHistoryTab</c>.</summary>
    public event EventHandler<HistoryReplayPayload>? OpenInTabRequested;

    /// <summary>Raised by the "Open as request" action; the host promotes the entry into an
    /// editable scratch tab via <c>OpenTabsViewModel.OpenHistoryAsScratch</c> so the user can
    /// tweak and save it into a collection.</summary>
    public event EventHandler<HistoryReplayPayload>? OpenAsRequestRequested;

    /// <summary>Raised by the "Save to collection…" action; the host opens the entry as a scratch
    /// draft and immediately runs the save-to-collection picker on it.</summary>
    public event EventHandler<HistoryReplayPayload>? SaveToCollectionRequested;

    /// <summary>Loads a row's full snapshot and raises <see cref="OpenAsRequestRequested"/> so the
    /// host opens it as an editable scratch draft (the "treat history like a scratch tab" path).</summary>
    [RelayCommand]
    public async Task OpenAsRequestAsync(HistoryRow? row)
    {
        if (row is null) return;
        try
        {
            OpenAsRequestRequested?.Invoke(this, await BuildPayloadAsync(row));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open history id {Id} as request", row.Id);
        }
    }

    /// <summary>Loads a row's full snapshot and raises <see cref="SaveToCollectionRequested"/> so
    /// the host promotes it into a collection (via a scratch draft + the save picker).</summary>
    [RelayCommand]
    public async Task SaveToCollectionAsync(HistoryRow? row)
    {
        if (row is null) return;
        try
        {
            SaveToCollectionRequested?.Invoke(this, await BuildPayloadAsync(row));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save history id {Id} to collection", row.Id);
        }
    }

    /// <summary>Switching workspace reloads the list so it shows only that workspace's history.</summary>
    partial void OnWorkspaceIdChanged(string? value) => _ = RefreshAsync();

    /// <summary>Debounced reload on each search edit. A short delay coalesces rapid keystrokes
    /// into a single query; a superseding edit cancels the pending reload.</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedSearchAsync(cts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(180, ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested) await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    // ----- commands --------------------------------------------------------

    /// <summary>Monotonic stamp guarding against interleaved reloads. Workspace switches, the
    /// search debounce, and sidebar activation can all trigger <see cref="RefreshAsync"/> close
    /// together; only the newest one is allowed to mutate <see cref="Items"/>, and an in-flight
    /// "load more" is dropped once a refresh supersedes it.</summary>
    private int _refreshGeneration;

    /// <summary>Loads the first page from SQLite. Call when the user activates the History panel.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var gen = ++_refreshGeneration;
        IsLoading = true;
        try
        {
            var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
            var rows = await _store.GetRangeAsync(offset: 0, limit: PageSize, workspaceId: WorkspaceId, search: search);
            // A newer refresh started while we awaited — let it own the list.
            if (gen != _refreshGeneration) return;
            Items.Clear();
            foreach (var r in rows) Items.Add(HistoryRow.From(r));
            _loadedCount = Items.Count;
            HasMore = rows.Count == PageSize;
            _lastOpenedRowId = null;
            UpdateEmptyState(search is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history");
        }
        finally
        {
            if (gen == _refreshGeneration) IsLoading = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsLoadingMore && !IsLoading;

    /// <summary>Fetches the next page from SQLite and appends to <see cref="Items"/>.
    /// Triggered by the explicit "Load more" link or the panel's scroll-near-bottom watcher.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        var gen = _refreshGeneration;
        IsLoadingMore = true;
        try
        {
            var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
            var rows = await _store.GetRangeAsync(offset: _loadedCount, limit: PageSize, workspaceId: WorkspaceId, search: search);
            // A refresh (workspace switch / new search) superseded this page — discard it so we
            // don't append the previous workspace's rows onto the new list.
            if (gen != _refreshGeneration) return;
            foreach (var r in rows) Items.Add(HistoryRow.From(r));
            _loadedCount += rows.Count;
            HasMore = rows.Count == PageSize;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load more history");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>Clears the CURRENT workspace's history (not every workspace's). The list empties
    /// to match.</summary>
    [RelayCommand]
    public async Task ClearAsync()
    {
        await _store.ClearAsync(WorkspaceId);
        Items.Clear();
        _loadedCount = 0;
        HasMore = false;
        SelectedRow = null;
        _lastOpenedRowId = null;
        UpdateEmptyState(!string.IsNullOrWhiteSpace(SearchText));
    }

    /// <summary>Recomputes the centered empty-state visibility + copy. <paramref name="filtered"/>
    /// selects the "no matches" wording over "no history yet".</summary>
    private void UpdateEmptyState(bool filtered)
    {
        IsListEmpty = Items.Count == 0;
        EmptyStateText = filtered ? NoMatchesText : NoHistoryText;
        EmptyStateTitle = filtered ? NoMatchesTitle : NoHistoryTitle;
        EmptyStateSubtitle = filtered ? NoMatchesSubtitle : NoHistorySubtitle;
    }

    /// <summary>Removes a single entry from the store and the bound list.</summary>
    [RelayCommand]
    public async Task DeleteAsync(HistoryRow? row)
    {
        if (row is null) return;
        try
        {
            await _store.DeleteAsync(row.Id).ConfigureAwait(true);
            if (ReferenceEquals(SelectedRow, row)) SelectedRow = null;
            if (Items.Remove(row))
            {
                _loadedCount = Math.Max(0, _loadedCount - 1);
                UpdateEmptyState(!string.IsNullOrWhiteSpace(SearchText));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete history id {Id}", row.Id);
        }
    }

    /// <summary>Builds a HAR 1.2 archive from the most recent entries. Limit matches what's
    /// loaded so the export reflects the user's current view of history.</summary>
    public async Task<string> ExportHarAsync()
    {
        var entries = await _store.GetRecentAsync(Math.Max(PageSize, _loadedCount), WorkspaceId);
        return HarExporter.Export(entries, creatorName: "Vegha");
    }

    /// <summary>Raised when the user clicks "Export to HAR". The host writes the produced
    /// content via a save dialog so the VM stays Avalonia-free.</summary>
    public event EventHandler? HarExportRequested;

    [RelayCommand]
    public void RequestHarExport() => HarExportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Records a new history entry from a completed request. Called by RequestEditorViewModel after Send.</summary>
    public async Task AppendAsync(
        string method, string url, int statusCode, long durationMs,
        string? body, string? errorMessage)
    {
        try
        {
            await _store.AppendAsync(method, url, statusCode, durationMs, body, errorMessage,
                workspaceId: WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append history entry");
        }
    }
}

/// <summary>Payload for <see cref="HistoryViewModel.OpenInTabRequested"/>. The host uses it
/// to build a <c>HistoryTabViewModel</c> with both request + response sides populated.</summary>
public sealed record HistoryReplayPayload(
    HistoryRow Row,
    string? RequestBlob,
    string? ResponseBody);

/// <summary>UI-friendly projection of a HistoryEntry.</summary>
public sealed record HistoryRow(
    long Id,
    string Method,
    string Url,
    int StatusCode,
    long DurationMs,
    DateTimeOffset TimestampUtc,
    string? ErrorMessage,
    string? RequestKind = null)
{
    public string TimeAgo => FormatRelative(TimestampUtc);
    public bool IsError => StatusCode == 0 || ErrorMessage is not null;
    public bool Is2xx => StatusCode is >= 200 and < 300;
    public bool Is4xx => StatusCode is >= 400 and < 500;
    public bool Is5xx => StatusCode >= 500;

    /// <summary>True for GraphQL sends — the panel shows the GraphQL mark instead of the
    /// method badge. Rows written by older builds have no kind and render as plain HTTP.</summary>
    public bool IsGraphQL => RequestKind == "graphql";

    public static HistoryRow From(HistoryEntry e) =>
        new(e.Id, e.Method, e.Url, e.StatusCode, e.DurationMs, e.TimestampUtc, e.ErrorMessage, e.RequestKind);

    private static string FormatRelative(DateTimeOffset ts)
    {
        var delta = DateTimeOffset.UtcNow - ts;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)   return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7)     return $"{(int)delta.TotalDays}d ago";
        return ts.LocalDateTime.ToString("yyyy-MM-dd");
    }
}
