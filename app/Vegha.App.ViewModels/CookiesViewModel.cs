using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Requests;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Cookies panel. The flat domain/path/name/value table is sourced from the
/// app-wide <see cref="CookieJarStore"/>. Supports Add (creates a new cookie), edit-in-
/// place (write back through Upsert), Delete, Clear, and a domain filter.
/// </summary>
public partial class CookiesViewModel : ObservableObject
{
    private readonly CookieJarStore _store;
    private readonly ILogger<CookiesViewModel> _logger;
    private readonly List<CookieRow> _all = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _domainFilter = string.Empty;

    public ObservableCollection<CookieRow> Items { get; } = new();

    public CookiesViewModel(CookieJarStore store, ILogger<CookiesViewModel> logger)
    {
        _store = store;
        _logger = logger;
    }

    partial void OnDomainFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void Refresh()
    {
        IsLoading = true;
        try
        {
            _all.Clear();
            foreach (var c in _store.GetAll())
            {
                var row = CookieRow.From(c);
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load cookies");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        IEnumerable<CookieRow> view = _all;
        if (!string.IsNullOrWhiteSpace(DomainFilter))
        {
            var needle = DomainFilter.Trim();
            view = view.Where(r => r.Domain.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var r in view) Items.Add(r);
    }

    [RelayCommand]
    public async Task DeleteAsync(CookieRow? row)
    {
        if (row is null) return;
        try
        {
            await _store.RemoveAsync(row.Domain, row.Path, row.Name);
            _all.Remove(row);
            Items.Remove(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete cookie");
        }
    }

    [RelayCommand]
    public async Task ClearAsync()
    {
        try
        {
            await _store.ClearAsync();
            _all.Clear();
            Items.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cookies");
        }
    }

    [RelayCommand]
    public async Task AddCookieAsync()
    {
        // Seed with empty fields; the user fills them in inline + an UpsertAsync writes
        // them back. The first non-empty Domain + Name pair becomes a real cookie.
        var row = new CookieRow();
        row.PropertyChanged += OnRowChanged;
        _all.Add(row);
        Items.Add(row);
        await Task.CompletedTask;
    }

    /// <summary>Inline-edit hook: when any of Domain/Path/Name/Value/Expires/HttpOnly/Secure
    /// changes on a row, persist via UpsertAsync. Skip rows that don't yet have Domain + Name.</summary>
    private async void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CookieRow row) return;
        if (string.IsNullOrWhiteSpace(row.Domain) || string.IsNullOrWhiteSpace(row.Name)) return;

        DateTime? expires = null;
        if (DateTime.TryParse(row.Expires, out var parsed)) expires = parsed.ToUniversalTime();

        try
        {
            await _store.UpsertAsync(row.Domain, row.Path, row.Name, row.Value,
                expires, row.HttpOnly, row.Secure);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cookie upsert failed");
        }
    }
}

public partial class CookieRow : ObservableObject
{
    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private string _path = "/";
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _expires = "session";
    [ObservableProperty] private bool _httpOnly;
    [ObservableProperty] private bool _secure;

    public CookieRow() { }

    public static CookieRow From(CookieRecord c) => new()
    {
        Domain = c.Domain,
        Path = c.Path,
        Name = c.Name,
        Value = c.Value,
        Expires = c.Expires?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "session",
        HttpOnly = c.HttpOnly,
        Secure = c.Secure,
    };
}
