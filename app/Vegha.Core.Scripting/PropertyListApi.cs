namespace Vegha.Core.Scripting;

/// <summary>
/// A single header / param entry exposed to scripts. Lowercase property names so user
/// scripts can write <c>h.key</c> / <c>h.value</c> per Bruno + Postman convention. Mutating
/// the object directly does not write back to the owning list — use <see cref="PropertyListApi.upsert"/>
/// for that.
/// </summary>
public sealed class HeaderItem
{
    public string key { get; set; } = string.Empty;
    public string value { get; set; } = string.Empty;
    public bool disabled { get; set; }
    public string? type { get; set; }

    public HeaderItem() { }
    public HeaderItem(string key, string value, bool disabled = false, string? type = null)
    {
        this.key = key;
        this.value = value;
        this.disabled = disabled;
        this.type = type;
    }
}

/// <summary>
/// Postman-style PropertyList facade — exposes <c>add</c>/<c>remove</c>/<c>upsert</c>/<c>get</c>
/// /<c>has</c>/<c>each</c>/<c>filter</c>/<c>map</c>/<c>all</c> over an underlying header list.
/// Mirrors <c>req.headers.*</c> / <c>res.headers.*</c> in Postman and <c>req.headerList.*</c>
/// in Bruno. Mutations are LIVE — they write back to the dictionary the caller supplied so
/// the HTTP send picks them up.
///
/// When <paramref name="readOnly"/> is true, mutators throw — used for the response side
/// where the wire is already gone by the time scripts run.
/// </summary>
public sealed class PropertyListApi
{
    private readonly IDictionary<string, string> _backing;
    private readonly bool _readOnly;
    private readonly StringComparer _keyComparer = StringComparer.OrdinalIgnoreCase;

    public PropertyListApi(IDictionary<string, string> backing, bool readOnly = false)
    {
        _backing = backing;
        _readOnly = readOnly;
    }

    // ---------- Bruno / Postman PropertyList surface ----------

    /// <summary>Adds a new entry. JS callers pass an object literal: <c>add({key, value})</c>
    /// or <c>add(key, value)</c>. Duplicates are allowed; use <see cref="upsert"/> for unique-key
    /// semantics.</summary>
    public void add(object? itemOrKey, object? maybeValue = null)
    {
        EnsureMutable();
        var (k, v) = Extract(itemOrKey, maybeValue);
        if (string.IsNullOrEmpty(k)) return;
        _backing[k] = v ?? string.Empty;
    }

    /// <summary>Inserts or updates by key. Bruno's preferred mutator.</summary>
    public void upsert(object? itemOrKey, object? maybeValue = null)
    {
        EnsureMutable();
        var (k, v) = Extract(itemOrKey, maybeValue);
        if (string.IsNullOrEmpty(k)) return;
        _backing[k] = v ?? string.Empty;
    }

    /// <summary>Removes the entry with the given key (case-insensitive). No-op when absent.</summary>
    public void remove(string key)
    {
        EnsureMutable();
        if (string.IsNullOrEmpty(key)) return;
        // Walk in case the backing dict is not case-insensitive.
        var match = _backing.Keys.FirstOrDefault(k => _keyComparer.Equals(k, key));
        if (match is not null) _backing.Remove(match);
    }

    /// <summary>Returns the value for <paramref name="key"/> (case-insensitive), or null when absent.</summary>
    public string? get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var kvp in _backing)
            if (_keyComparer.Equals(kvp.Key, key)) return kvp.Value;
        return null;
    }

    /// <summary>True when an entry with <paramref name="key"/> exists.</summary>
    public bool has(string key) => get(key) is not null;

    /// <summary>Visits every entry. Bruno's <c>.each(fn)</c>.</summary>
    public void each(Delegate fn)
    {
        if (fn is null) return;
        foreach (var item in Snapshot()) fn.DynamicInvoke(item);
    }

    /// <summary>Returns entries for which <paramref name="predicate"/> is truthy.</summary>
    public List<HeaderItem> filter(Delegate predicate)
    {
        var result = new List<HeaderItem>();
        if (predicate is null) return result;
        foreach (var item in Snapshot())
        {
            var ret = predicate.DynamicInvoke(item);
            if (IsTruthy(ret)) result.Add(item);
        }
        return result;
    }

    /// <summary>Returns the result of <paramref name="selector"/> applied to every entry.</summary>
    public List<object?> map(Delegate selector)
    {
        var result = new List<object?>();
        if (selector is null) return result;
        foreach (var item in Snapshot()) result.Add(selector.DynamicInvoke(item));
        return result;
    }

    /// <summary>Materializes the current list as <see cref="HeaderItem"/>s — used by scripts
    /// that want to enumerate without callbacks.</summary>
    public List<HeaderItem> all() => Snapshot();

    /// <summary>Number of entries (Bruno parity).</summary>
    public int count() => _backing.Count;

    // ---------- internals ----------

    private List<HeaderItem> Snapshot()
    {
        var list = new List<HeaderItem>(_backing.Count);
        foreach (var (k, v) in _backing) list.Add(new HeaderItem(k, v));
        return list;
    }

    private void EnsureMutable()
    {
        if (_readOnly)
            throw new InvalidOperationException(
                "This PropertyList is read-only (response headers can't be mutated from scripts).");
    }

    private static (string Key, string? Value) Extract(object? itemOrKey, object? maybeValue)
    {
        // Two call shapes Bruno/Postman accept: add({key,value}) or add("X","y").
        if (itemOrKey is string s)
            return (s, maybeValue?.ToString());
        if (itemOrKey is HeaderItem hi)
            return (hi.key, hi.value);
        if (itemOrKey is IDictionary<string, object?> dict)
        {
            var k = dict.TryGetValue("key", out var ko) ? ko?.ToString() : null;
            var v = dict.TryGetValue("value", out var vo) ? vo?.ToString() : null;
            return (k ?? string.Empty, v);
        }
        // Jint typically marshals JS object literals as ExpandoObject / DynamicObject.
        if (itemOrKey is System.Dynamic.IDynamicMetaObjectProvider)
        {
            string? key = null;
            string? val = null;
            foreach (var prop in itemOrKey.GetType().GetProperties())
            {
                if (prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase))
                    key = prop.GetValue(itemOrKey)?.ToString();
                else if (prop.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
                    val = prop.GetValue(itemOrKey)?.ToString();
            }
            return (key ?? string.Empty, val);
        }
        // Last-ditch: reflect anonymous-type / POCO with "key"/"value" props.
        if (itemOrKey is not null)
        {
            var t = itemOrKey.GetType();
            var kp = t.GetProperty("key") ?? t.GetProperty("Key");
            var vp = t.GetProperty("value") ?? t.GetProperty("Value");
            var k = kp?.GetValue(itemOrKey)?.ToString();
            var v = vp?.GetValue(itemOrKey)?.ToString();
            return (k ?? string.Empty, v);
        }
        return (string.Empty, null);
    }

    private static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s),
        double d => d != 0,
        int i => i != 0,
        _ => true
    };
}
