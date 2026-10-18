using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Services;
using Vegha.Core.Engine;

namespace Vegha.App.ViewModels.Scriptless;

/// <summary>One row of the Runtime variables list.</summary>
public sealed record RuntimeVariableRow(string Name, string Value, string Provenance);

/// <summary>
/// Bindable view of the session <see cref="RuntimeVariableStore"/>: name, value and where each
/// value came from, with Remove / Clear / Promote-to-environment. Shown in the Environments
/// panel and the Tests tab's runtime flyout.
/// </summary>
public sealed partial class RuntimeVariablesViewModel : ObservableObject
{
    private readonly RuntimeVariableStore _store;
    private readonly Action<string, string>? _promote;

    public RuntimeVariablesViewModel(RuntimeVariableStore store, Action<string, string>? promoteToEnvironment = null)
    {
        _store = store;
        _promote = promoteToEnvironment;
        _store.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<RuntimeVariableRow> Rows { get; } = new();

    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private int _count;

    public bool CanPromote => _promote is not null;

    public void Refresh()
    {
        Rows.Clear();
        foreach (var e in _store.Entries) Rows.Add(new RuntimeVariableRow(e.Name, e.Value, _store.Describe(e)));
        Count = Rows.Count;
        HasRows = Rows.Count > 0;
    }

    [RelayCommand]
    private void Remove(RuntimeVariableRow? row)
    {
        if (row is not null) _store.Remove(row.Name);
    }

    [RelayCommand]
    private void Clear() => _store.Clear();

    /// <summary>Copies the value into the active environment (persisting it) and drops the
    /// runtime copy so the environment value is what resolves from now on.</summary>
    [RelayCommand]
    private void Promote(RuntimeVariableRow? row)
    {
        if (row is null || _promote is null) return;
        _promote(row.Name, row.Value);
        _store.Remove(row.Name);
    }
}
