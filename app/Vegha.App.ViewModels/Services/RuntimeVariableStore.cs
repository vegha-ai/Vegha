namespace Vegha.App.ViewModels.Services;

/// <summary>
/// Session-scoped runtime variables (Bruno's <c>bru.setVar</c> / "runtime variables") shared by
/// every open request tab. Filled by scriptless captures with Runtime scope, linked requests and
/// script <c>bru.setVar</c> calls; never written to disk. Each value remembers where it came
/// from so hovers can say "captured from Login · body access_token · 2 min ago".
///
/// Runtime variables sit above environment and collection variables and below the request's own
/// Vars when <c>{{name}}</c> is resolved. Cleared on workspace switch. UI-thread only.
/// </summary>
public sealed class RuntimeVariableStore
{
    /// <summary>One runtime variable plus its provenance.</summary>
    public sealed record Entry(string Name, string Value, string? SourceRequest, string? SourceDetail, DateTimeOffset At);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public RuntimeVariableStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>Raised after any change (set / remove / clear).</summary>
    public event EventHandler? Changed;

    public int Count => _entries.Count;

    /// <summary>Entries sorted by name.</summary>
    public IReadOnlyList<Entry> Entries => _entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyDictionary<string, string> Snapshot() =>
        _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

    public bool TryGet(string name, out Entry entry) => _entries.TryGetValue(name, out entry!);

    public void Set(string name, string value, string? sourceRequest = null, string? sourceDetail = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _entries[name] = new Entry(name, value, sourceRequest, sourceDetail, _clock());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets several variables with one change notification. <paramref name="detail"/>
    /// supplies a per-variable provenance detail (e.g. the capture expression).</summary>
    public void SetMany(IReadOnlyDictionary<string, string> values, string? sourceRequest = null,
        Func<string, string?>? detail = null)
    {
        var changed = false;
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            _entries[name] = new Entry(name, value, sourceRequest, detail?.Invoke(name), _clock());
            changed = true;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string name)
    {
        if (_entries.Remove(name)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"captured from Login · res.body.access_token · 2 min ago" — for hovers.</summary>
    public string Describe(Entry e)
    {
        var parts = new List<string> { "runtime" };
        if (!string.IsNullOrEmpty(e.SourceRequest)) parts[0] = "captured from " + e.SourceRequest;
        if (!string.IsNullOrEmpty(e.SourceDetail)) parts.Add(e.SourceDetail!);
        parts.Add(FormatAge(_clock() - e.At));
        return string.Join(" · ", parts);
    }

    public static string FormatAge(TimeSpan age) =>
        age.TotalSeconds < 10 ? "just now"
        : age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s ago"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";
}
