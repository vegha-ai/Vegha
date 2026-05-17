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

    private async Task RaiseOpenInTabAsync(HistoryRow row)
    {
        try
        {
            var entryTask = _store.GetByIdAsync(row.Id);
            var blobTask = _store.GetRequestBlobAsync(row.Id);
            await Task.WhenAll(entryTask, blobTask).ConfigureAwait(true);
            var payload = new HistoryReplayPayload(
                Row: row,
                RequestBlob: blobTask.Result,
                ResponseBody: entryTask.Result?.ResponseBodyPreview);
            OpenInTabRequested?.Invoke(this, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open history tab for id {Id}", row.Id);
        }
    }

    /// <summary>Raised when the user selects a history entry; the host turns the payload
    /// into a tab via <c>OpenTabsViewModel.OpenHistoryTab</c>.</summary>
    public event EventHandler<HistoryReplayPayload>? OpenInTabRequested;

    // ----- commands --------------------------------------------------------

    /// <summary>Loads the first page from SQLite. Call when the user activates the History panel.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var rows = await _store.GetRangeAsync(offset: 0, limit: PageSize);
            Items.Clear();
            foreach (var r in rows) Items.Add(HistoryRow.From(r));
            _loadedCount = Items.Count;
            HasMore = rows.Count == PageSize;
            _lastOpenedRowId = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsLoadingMore && !IsLoading;

    /// <summary>Fetches the next page from SQLite and appends to <see cref="Items"/>.
    /// Triggered by the explicit "Load more" link or the panel's scroll-near-bottom watcher.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        IsLoadingMore = true;
        try
        {
            var rows = await _store.GetRangeAsync(offset: _loadedCount, limit: PageSize);
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

    [RelayCommand]
    public async Task ClearAsync()
    {
        await _store.ClearAsync();
        Items.Clear();
        _loadedCount = 0;
        HasMore = false;
        SelectedRow = null;
        _lastOpenedRowId = null;
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
            if (Items.Remove(row)) _loadedCount = Math.Max(0, _loadedCount - 1);
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
        var entries = await _store.GetRecentAsync(Math.Max(PageSize, _loadedCount));
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
            await _store.AppendAsync(method, url, statusCode, durationMs, body, errorMessage);
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
    string? ErrorMessage)
{
    public string TimeAgo => FormatRelative(TimestampUtc);
    public bool IsError => StatusCode == 0 || ErrorMessage is not null;
    public bool Is2xx => StatusCode is >= 200 and < 300;
    public bool Is4xx => StatusCode is >= 400 and < 500;
    public bool Is5xx => StatusCode >= 500;

    public static HistoryRow From(HistoryEntry e) =>
        new(e.Id, e.Method, e.Url, e.StatusCode, e.DurationMs, e.TimestampUtc, e.ErrorMessage);

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
