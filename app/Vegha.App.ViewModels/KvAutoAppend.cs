using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Vegha.App.ViewModels;

/// <summary>
/// Keeps one blank "ghost" row at the tail of a key-value row collection so users type straight
/// into the table instead of clicking "+ Add row" (Bruno parity). Typing into the ghost row
/// spawns the next one; deleting the ghost re-adds it. Ghost rows must be filtered out of every
/// save/serialize path — they are UI chrome, not data.
/// </summary>
public static class KvAutoAppend
{
    /// <summary>Appends a blank row when the collection is empty or its last row has content.
    /// Call from load paths (inside their loading guard) so the structural add doesn't count
    /// as a user edit.</summary>
    public static void EnsureTrailingBlank<T>(
        ObservableCollection<T> rows, Func<T> factory, Func<T, bool> isBlank)
    {
        if (rows.Count == 0 || !isBlank(rows[^1])) rows.Add(factory());
    }

    /// <summary>Wires the live behavior: when the tail row gains content, append a fresh blank
    /// row; when a Remove leaves no blank tail, restore it. <paramref name="suppress"/> gates
    /// both reactions off during VM load (the load path calls
    /// <see cref="EnsureTrailingBlank{T}"/> itself, inside its loading guard).</summary>
    public static void Wire<T>(
        ObservableCollection<T> rows, Func<T> factory, Func<T, bool> isBlank,
        Func<bool>? suppress = null)
        where T : INotifyPropertyChanged
    {
        void OnRowChanged(object? s, PropertyChangedEventArgs e)
        {
            if (suppress?.Invoke() == true) return;
            // Only the tail row spawns a new ghost — edits to (or blanking of) middle rows
            // never mutate the collection, so focus is never yanked mid-keystroke.
            if (s is T row && rows.Count > 0 && ReferenceEquals(rows[^1], row) && !isBlank(row))
                rows.Add(factory());
        }

        foreach (var r in rows) r.PropertyChanged += OnRowChanged;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Clear() carries no OldItems, so re-subscribe whatever survives. Handlers left
                // on discarded rows are inert — the ReferenceEquals tail check rejects them —
                // and don't root the rows (the rows hold the handler, not vice versa).
                foreach (var r in rows) { r.PropertyChanged -= OnRowChanged; r.PropertyChanged += OnRowChanged; }
                return;
            }
            if (e.NewItems is not null) foreach (T r in e.NewItems) r.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null) foreach (T r in e.OldItems) r.PropertyChanged -= OnRowChanged;
            if (suppress?.Invoke() == true) return;
            if (e.Action == NotifyCollectionChangedAction.Remove)
                EnsureTrailingBlank(rows, factory, isBlank);
        };
    }
}
