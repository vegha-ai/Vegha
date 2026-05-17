using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels;

/// <summary>What kind of thing a result row points at — drives both the icon prefix
/// shown in the palette and the activation action when the user picks the row.</summary>
public enum SearchResultKind { OpenTab, Request, Environment, Recent, Setting }

public sealed record SearchResult(
    SearchResultKind Kind,
    string Title,
    string Subtitle,
    object Payload);

/// <summary>
/// Drives the Ctrl-K search palette. Indexes:
///   - Open tabs (current + draft)
///   - All requests across loaded collections
///   - Active environment's variable names
///   - Recent collections from RecentItemsStore
///   - Top-level settings entries (theme, proxy, timeout, etc.)
/// Filters with case-insensitive substring + simple word-boundary scoring so
/// "uers em" lifts users.email-like matches above unrelated ones.
/// </summary>
public partial class SearchPaletteViewModel : ObservableObject
{
    private readonly OpenTabsViewModel _openTabs;
    private readonly CollectionsViewModel _collections;
    private readonly RecentItemsStore? _recent;

    public ObservableCollection<SearchResult> Results { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private SearchResult? _selected;

    public SearchPaletteViewModel(
        OpenTabsViewModel openTabs,
        CollectionsViewModel collections,
        RecentItemsStore? recent = null)
    {
        _openTabs = openTabs;
        _collections = collections;
        _recent = recent;
    }

    partial void OnQueryChanged(string value) => Refresh();

    [RelayCommand]
    public void Open()
    {
        IsOpen = true;
        Query = string.Empty;
        Refresh();
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    public void Activate(SearchResult? result)
    {
        if (result is null) return;
        switch (result.Kind)
        {
            case SearchResultKind.OpenTab when result.Payload is RequestTabViewModel tab:
                _openTabs.ActiveTab = tab;
                break;
            case SearchResultKind.Request when result.Payload is CollectionItemViewModel item:
                _collections.OpenRequestCommand.Execute(item);
                break;
            case SearchResultKind.Recent when result.Payload is string path:
                _collections.LoadFromDirectory(path);
                break;
            case SearchResultKind.Environment:
                // Setting the active env can be done via host wiring; for now just bubble.
                break;
        }
        Close();
    }

    /// <summary>Rebuilds <see cref="Results"/> from the current sources, ranked by
    /// <see cref="Score"/> against <see cref="Query"/>. Empty query shows top open tabs +
    /// recent collections.</summary>
    public void Refresh()
    {
        Results.Clear();
        var q = Query?.Trim() ?? string.Empty;

        var candidates = BuildIndex().ToList();
        if (string.IsNullOrEmpty(q))
        {
            // Empty-query landing: open tabs + recents + a few requests.
            foreach (var c in candidates.Where(r => r.Kind == SearchResultKind.OpenTab).Take(8)) Results.Add(c);
            foreach (var c in candidates.Where(r => r.Kind == SearchResultKind.Recent).Take(8)) Results.Add(c);
            foreach (var c in candidates.Where(r => r.Kind == SearchResultKind.Request).Take(20)) Results.Add(c);
            return;
        }

        var ranked = candidates
            .Select(c => (Result: c, Score: Score(c, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Result.Title.Length)
            .Take(40)
            .Select(x => x.Result);
        foreach (var r in ranked) Results.Add(r);

        Selected = Results.FirstOrDefault();
    }

    public IEnumerable<SearchResult> BuildIndex()
    {
        // Open tabs.
        foreach (var tab in _openTabs.Tabs)
            yield return new SearchResult(SearchResultKind.OpenTab, tab.Name,
                tab.Method + " " + (tab.SourcePath ?? "draft"), tab);

        // All requests across loaded collections.
        foreach (var root in _collections.Roots)
            foreach (var item in EnumerateLeaves(root))
                yield return new SearchResult(SearchResultKind.Request, item.Name,
                    item.MethodLabel + " · " + (item.SourcePath ?? item.Path),
                    item);

        // Active environment vars.
        if (_collections.ActiveEnvironment is { } env)
            foreach (var v in env.Variables)
                yield return new SearchResult(SearchResultKind.Environment,
                    v.Name, $"{env.Name} · {v.Value}", v);

        // Recent collections.
        if (_recent is not null)
            foreach (var r in _recent.Load())
                yield return new SearchResult(SearchResultKind.Recent,
                    System.IO.Path.GetFileName(r.Path), r.Path, r.Path);

        // Settings entries — fixed surface for now.
        foreach (var (title, sub) in s_settings)
            yield return new SearchResult(SearchResultKind.Setting, title, sub, title);
    }

    private static readonly (string, string)[] s_settings =
    {
        ("Theme", "dark / light / system"),
        ("HTTP proxy", "global proxy URL"),
        ("Trusted CAs", "custom root CA list"),
        ("Default timeout", "request timeout seconds"),
    };

    private static IEnumerable<CollectionItemViewModel> EnumerateLeaves(CollectionNodeViewModel node)
    {
        if (node is CollectionItemViewModel leaf) { yield return leaf; yield break; }
        foreach (var child in node.Children)
            foreach (var d in EnumerateLeaves(child)) yield return d;
    }

    /// <summary>Word-boundary substring score. Matches at the start of a word weight
    /// higher than mid-word matches; multi-word queries require all tokens to match
    /// somewhere in the title or subtitle. Returns 0 for non-matches.</summary>
    public static int Score(SearchResult r, string query)
    {
        var haystack = (r.Title + " " + r.Subtitle).ToLowerInvariant();
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 1;
        var score = 0;
        foreach (var t in tokens)
        {
            var idx = haystack.IndexOf(t, StringComparison.Ordinal);
            if (idx < 0) return 0;
            score += idx == 0 ? 100 : (idx > 0 && haystack[idx - 1] is ' ' or '/' or '.' or '-' or '_' ? 50 : 10);
        }
        return score;
    }
}
