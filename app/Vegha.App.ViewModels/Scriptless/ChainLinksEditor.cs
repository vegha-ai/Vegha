using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Requests;

namespace Vegha.App.ViewModels.Scriptless;

/// <summary>A request a linked-request row can point at: what the row stores (<see cref="Path"/>)
/// plus what the picker shows — method, name, folder and the variables it captures.</summary>
public sealed record ChainCandidate(
    string Path,
    string Name,
    string Method,
    RequestKind Kind,
    string Folder,
    IReadOnlyList<string> Captures,
    string? SourcePath)
{
    public string MethodLabel => Kind switch
    {
        RequestKind.WebSocket => "WS",
        RequestKind.Grpc => "gRPC",
        RequestKind.GraphQL => "GQL",
        _ => Method.ToUpperInvariant(),
    };

    public bool HasCaptures => Captures.Count > 0;
    public string CapturesText => string.Join("  ", Captures.Select(c => "{{" + c + "}}"));
    public bool HasFolder => Folder.Length > 0;

    private string SearchText => $"{MethodLabel} {Name} {Folder} {Path}".ToLowerInvariant();

    /// <summary>The picker box shows the request's name once chosen.</summary>
    public override string ToString() => Name;

    /// <summary>Every request in <paramref name="collection"/> except <paramref name="excludeSourcePath"/>,
    /// root requests first, then folder by folder.</summary>
    public static IReadOnlyList<ChainCandidate> Of(Collection collection, string? collectionRoot, string? excludeSourcePath = null)
    {
        var ctx = new ChainContext(collection, collectionRoot);
        return ctx.Enumerate()
            .Where(x => excludeSourcePath is null || x.Request.SourcePath is null
                        || !string.Equals(x.Request.SourcePath, excludeSourcePath, StringComparison.OrdinalIgnoreCase))
            .Select(x => new ChainCandidate(
                ctx.PathOf(x.Request, x.FolderChain),
                x.Request.Name,
                x.Request.Method,
                x.Request.Kind,
                string.Join(" / ", x.FolderChain.Select(f => f.Name)),
                x.Request.PostResponseVars.Concat(x.Request.PostResponseEnvVars).Concat(x.Request.PostResponseGlobalVars)
                    .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => c.Name.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                x.Request.SourcePath))
            .OrderBy(c => c.Folder.Length == 0 ? 0 : 1)
            .ThenBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Picker filter: every whitespace-separated term must appear in the method, name,
    /// folder or path — so "post login" or "outage peco" narrow a big collection quickly.</summary>
    public static bool Matches(string? query, object? item)
    {
        if (item is not ChainCandidate c) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;
        var hay = c.SearchText;
        return query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
    }
}

/// <summary>A link applied from above (the collection or an enclosing folder), shown read-only.</summary>
public sealed record InheritedChainLink(string Source, string Method, string Name, string Condition, bool Enabled);

/// <summary>
/// The linked-requests editor shared by the request editor and folder / collection settings:
/// owns the request catalog the pickers offer, keeps each <see cref="ChainLinkRow"/> resolved
/// against it (step number, available requests, what the target captures), and the row
/// commands — reorder, remove, open. The rows themselves stay in the host's collection, which
/// is what gets saved.
/// </summary>
public sealed partial class ChainLinksEditor : ObservableObject
{
    private IReadOnlyList<ChainCandidate> _candidates = Array.Empty<ChainCandidate>();
    private IReadOnlyList<string> _knownVariables = Array.Empty<string>();

    public ChainLinksEditor(ObservableCollection<ChainLinkRow> rows)
    {
        Rows = rows;
        Rows.CollectionChanged += OnRowsChanged;
        foreach (var r in Rows) Attach(r);
        Refresh();
    }

    public ObservableCollection<ChainLinkRow> Rows { get; }

    /// <summary>Links inherited from the collection / enclosing folders (request editor only).</summary>
    public ObservableCollection<InheritedChainLink> Inherited { get; } = new();

    public bool HasInherited => Inherited.Count > 0;

    /// <summary>True when the host can open a linked request (shows the rows' Open button).</summary>
    [ObservableProperty] private bool _canOpen;

    /// <summary>Raised by a row's Open button with the request to open.</summary>
    public event EventHandler<ChainCandidate>? OpenRequested;

    public IReadOnlyList<ChainCandidate> Candidates
    {
        get => _candidates;
        set
        {
            _candidates = value ?? Array.Empty<ChainCandidate>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCandidates));
            Refresh();
        }
    }

    public bool HasCandidates => _candidates.Count > 0;

    /// <summary>Variable names in scope (environment, collection…) offered by the variable pickers
    /// after the linked request's own captures.</summary>
    public IReadOnlyList<string> KnownVariables
    {
        get => _knownVariables;
        set
        {
            _knownVariables = value ?? Array.Empty<string>();
            foreach (var r in Rows) r.RefreshFromEditor();
        }
    }

    public void SetInherited(IEnumerable<InheritedChainLink> links)
    {
        Inherited.Clear();
        foreach (var l in links) Inherited.Add(l);
        OnPropertyChanged(nameof(HasInherited));
    }

    /// <summary>The request a link points at, resolved like the engine: exact path, else a
    /// unique request name (hand-written <c>Login: always</c>).</summary>
    public ChainCandidate? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var wanted = RequestComposition.NormalizeChainPath(path);
        var exact = _candidates.FirstOrDefault(c => string.Equals(c.Path, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var bare = wanted.EndsWith(".bru", StringComparison.OrdinalIgnoreCase) ? wanted[..^4] : wanted;
        var byName = _candidates.Where(c => string.Equals(c.Name, bare, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Requests <paramref name="row"/> may pick: all but those other rows already link.</summary>
    internal IReadOnlyList<ChainCandidate> AvailableFor(ChainLinkRow row)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in Rows)
            if (!ReferenceEquals(other, row) && !other.IsBlank)
                taken.Add(Resolve(other.RequestPath)?.Path ?? other.RequestPath);
        return _candidates.Where(c => !taken.Contains(c.Path)).ToList();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void MoveUp(ChainLinkRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(ChainLinkRow? row) => Move(row, +1);

    [RelayCommand]
    private void Remove(ChainLinkRow? row)
    {
        if (row is not null && !row.IsBlank) Rows.Remove(row);
    }

    [RelayCommand]
    private void Open(ChainLinkRow? row)
    {
        if (row?.Target is { } target) OpenRequested?.Invoke(this, target);
    }

    private void Move(ChainLinkRow? row, int delta)
    {
        if (row is null || row.IsBlank) return;
        var from = Rows.IndexOf(row);
        var to = from + delta;
        // The trailing ghost row stays last.
        if (from < 0 || to < 0 || to >= Rows.Count || Rows[to].IsBlank) return;
        Rows.Move(from, to);
    }

    // ------------------------------------------------------------------ bookkeeping

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var r in Rows) Attach(r);
        if (e.NewItems is not null) foreach (ChainLinkRow r in e.NewItems) Attach(r);
        Refresh();
    }

    private void Attach(ChainLinkRow row)
    {
        if (ReferenceEquals(row.Editor, this)) return;
        row.Editor = this;
        row.PropertyChanged += (s, e) =>
        {
            // Another row choosing a request changes what the others may pick.
            if (e.PropertyName == nameof(ChainLinkRow.RequestPath) && ReferenceEquals(row.Editor, this)) Refresh();
        };
    }

    private bool _refreshing;

    /// <summary>Re-numbers the rows and re-resolves them against the catalog.</summary>
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var linked = Rows.Where(r => !r.IsBlank).ToList();
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                var step = linked.IndexOf(row);
                row.SetPosition(step + 1, step == 0, step == linked.Count - 1);
                row.RefreshFromEditor();
            }
        }
        finally { _refreshing = false; }
    }
}
