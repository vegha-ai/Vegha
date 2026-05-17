using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.FileFormat;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Workspace tab that hosts the environment variable editor. Mirrors how
/// SoapRequestTabViewModel wraps a SoapWorkspaceViewModel — the tab strip
/// renders the env name + a leaf icon, the workspace area renders the variable
/// grid driven by <see cref="EnvironmentTabViewModel"/>.
/// </summary>
public sealed partial class EnvironmentTabViewModel : RequestTabViewModel
{
    private readonly Func<DomainEnv, Task>? _saveAsync;
    private DomainEnv _env;

    /// <summary>The active env snapshot — Save publishes the row state back via
    /// <c>with</c>-clone and the host persists through CollectionStore.</summary>
    public DomainEnv Environment
    {
        get => _env;
        private set { _env = value; OnPropertyChanged(); }
    }

    public ObservableCollection<EnvVarRow> Variables { get; } = new();

    [ObservableProperty] private string? _statusMessage;

    public override object Workspace => this;

    public EnvironmentTabViewModel(DomainEnv env, string id, Func<DomainEnv, Task>? saveAsync)
    {
        _env = env;
        _saveAsync = saveAsync;
        Id = id;
        Name = env.Name;
        Method = "ENV";
        Kind = Vegha.Core.Domain.RequestKind.Http;  // tab type isn't a request kind; reuse Http for icon defaults
        Hydrate();
    }

    private void Hydrate()
    {
        Variables.Clear();
        var secrets = new HashSet<string>(_env.SecretVariables, StringComparer.Ordinal);
        foreach (var v in _env.Variables)
        {
            Variables.Add(new EnvVarRow
            {
                Name = v.Name,
                Value = v.Value,
                IsSecret = secrets.Contains(v.Name),
                IsEnabled = v.Enabled,
            });
        }
    }

    [RelayCommand]
    private void AddVariable() =>
        Variables.Add(new EnvVarRow { Name = string.Empty, Value = string.Empty, IsEnabled = true });

    [RelayCommand]
    private void RemoveVariable(EnvVarRow? row)
    {
        if (row is not null) Variables.Remove(row);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_saveAsync is null) { StatusMessage = "No workspace bound."; return; }
        try
        {
            var newVars = Variables
                .Where(r => !string.IsNullOrEmpty(r.Name))
                .Select(r => new Vegha.Core.Domain.KvPair(r.Name, r.Value, r.IsEnabled))
                .ToList();
            var newSecrets = Variables
                .Where(r => !string.IsNullOrEmpty(r.Name) && r.IsSecret)
                .Select(r => r.Name)
                .ToList();
            var updated = _env with { Variables = newVars, SecretVariables = newSecrets };
            await _saveAsync(updated);
            Environment = updated;
            StatusMessage = $"Saved “{updated.Name}”.";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }
}
