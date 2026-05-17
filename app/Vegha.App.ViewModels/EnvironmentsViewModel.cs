using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.FileFormat;
using Vegha.Core.Importers;
using Microsoft.Extensions.Logging;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Environments sidebar — lists every loaded environment, lets the user
/// activate one, and edits its variables in-place. Save writes the full collection
/// back via CollectionStore so env files stay in lockstep with disk.
/// </summary>
public partial class EnvironmentsViewModel : ObservableObject
{
    private readonly CollectionsViewModel _collections;
    private readonly WorkspacesViewModel _workspaces;
    private readonly Vegha.Integrations.Secrets.SecretRegistry _secretRegistry;
    private readonly ILogger<EnvironmentsViewModel> _logger;

    /// <summary>Names of the secret providers currently registered for the active
    /// collection. Drives the per-variable "bind to secret manager" picker dropdown.</summary>
    public IReadOnlyList<string> ProviderNames =>
        _secretRegistry.ProviderNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Free-text filter applied to <see cref="Filtered"/>. Empty string = show all.
    /// Matching is case-insensitive substring on env Name.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    public DomainEnv? Active => _collections.ActiveEnvironment;
    /// <summary>Collection-scoped envs only. The left-toolbar panel never shows workspace /
    /// global envs — those live in the WorkspaceEditor dialog. Without this scoping the panel
    /// double-listed every workspace env alongside the active collection's envs.</summary>
    public ObservableCollection<DomainEnv> All => _collections.CollectionEnvironments;

    /// <summary>The env currently being edited in the master/detail's right pane. Driven by
    /// the ListBox's two-way SelectedItem binding. Mirrors WorkspaceTabViewModel's model so
    /// both env surfaces have the same shape.</summary>
    [ObservableProperty]
    private DomainEnv? _selectedEnvironment;

    /// <summary>Editable variable rows for <see cref="SelectedEnvironment"/>. Rebuilt by
    /// <see cref="HydrateVariables"/> whenever the selection or env identity changes.</summary>
    public ObservableCollection<EnvVarRow> Variables { get; } = new();

    [ObservableProperty] private bool _isDirty;

    partial void OnSelectedEnvironmentChanged(DomainEnv? value)
    {
        HydrateVariables();
        // Selecting an env in the master list also activates it for variable substitution
        // — matches the workspace dialog UX where the selected env is the one in use, and
        // drives the green check-mark indicator on the row that's the current active env.
        if (value is not null && !ReferenceEquals(_collections.ActiveEnvironment, value))
            _collections.ActiveEnvironment = value;
    }

    private void HydrateVariables()
    {
        Variables.Clear();
        if (SelectedEnvironment is null) { IsDirty = false; return; }
        var secrets = new HashSet<string>(SelectedEnvironment.SecretVariables, StringComparer.Ordinal);
        var providers = ProviderNames;
        foreach (var v in SelectedEnvironment.Variables)
        {
            Variables.Add(new EnvVarRow
            {
                Name = v.Name,
                Value = v.Value,
                IsSecret = secrets.Contains(v.Name),
                IsEnabled = v.Enabled,
                ProviderNames = providers,
            });
        }
        IsDirty = false;
    }

    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(new EnvVarRow
        {
            Name = string.Empty, Value = string.Empty, IsEnabled = true,
            ProviderNames = ProviderNames,
        });
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveVariable(EnvVarRow? row)
    {
        if (row is null) return;
        Variables.Remove(row);
        IsDirty = true;
    }

    [RelayCommand]
    private void Reset()
    {
        HydrateVariables();
        StatusMessage = "Reverted unsaved changes.";
    }

    /// <summary>Persists the row edits back to the selected env's .env.json and refreshes the
    /// shared state via <see cref="SaveEnvironmentAsync"/>. The save flow drives
    /// <see cref="CollectionsViewModel.ReplaceEnvironment"/> so the top-bar pill and other
    /// bindings reflect the new variables / color immediately.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedEnvironment is null) { StatusMessage = "No environment selected."; return; }
        var newVars = Variables
            .Where(r => !string.IsNullOrEmpty(r.Name))
            .Select(r => new Vegha.Core.Domain.KvPair(r.Name, r.Value, r.IsEnabled))
            .ToList();
        var newSecrets = Variables
            .Where(r => !string.IsNullOrEmpty(r.Name) && r.IsSecret)
            .Select(r => r.Name)
            .ToList();
        var previous = SelectedEnvironment;
        var updated = SelectedEnvironment with { Variables = newVars, SecretVariables = newSecrets };

        await SaveEnvironmentAsync(updated);
        // SaveEnvironmentAsync already swaps via ReplaceEnvironment; pin the new instance as
        // the selection so the editor's binding doesn't drop to null after the list update.
        SelectedEnvironment = updated;
        IsDirty = false;
        StatusMessage = $"Saved “{updated.Name}”.";
    }

    /// <summary>The subset of <see cref="All"/> visible after applying <see cref="SearchText"/>.
    /// Bound by the panel's ItemsControl. Kept in sync with <see cref="All"/>'s mutations
    /// (Add/Remove/Replace/Reset) so the user sees imports / deletes immediately.</summary>
    public ObservableCollection<DomainEnv> Filtered { get; } = new();

    public EnvironmentsViewModel(
        CollectionsViewModel collections,
        WorkspacesViewModel workspaces,
        Vegha.Integrations.Secrets.SecretRegistry secretRegistry,
        ILogger<EnvironmentsViewModel> logger)
    {
        _collections = collections;
        _workspaces = workspaces;
        _secretRegistry = secretRegistry;
        _logger = logger;

        _collections.PropertyChanged += OnCollectionsPropertyChanged;
        All.CollectionChanged += OnAllCollectionChanged;
        RebuildFiltered();
        Refresh();
    }

    // Selection-restore state. Survives a Clear→Add reload cycle so the editor
    // re-pins the just-saved env after the file watcher's downstream
    // RebuildEnvironments runs. The "wasn't this fixed?" history: the prior fix
    // captured keepId inside RebuildFiltered, but Filtered.Clear() raises Reset
    // synchronously and the ListBox nulls SelectedEnvironment before the captured
    // value can be used. These fields live across events so the snapshot survives.
    private string? _pendingRestoreId;
    private string? _pendingRestoreName;

    private void OnAllCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // On any "destructive" change to the source list (Reset = Clear, or Replace where
        // identity may differ), snapshot the current selection so we can restore it after
        // the rebuild — even if it spans multiple subsequent Add events.
        if (SelectedEnvironment is not null &&
            (e.Action == NotifyCollectionChangedAction.Reset
             || e.Action == NotifyCollectionChangedAction.Replace
             || e.Action == NotifyCollectionChangedAction.Remove))
        {
            _pendingRestoreId = SelectedEnvironment.Id;
            _pendingRestoreName = SelectedEnvironment.Name;
        }

        // Targeted Replace path: when CollectionEnvironments swaps an item (rename / color /
        // save), mirror that single swap into Filtered without Clear+Add. Filtered.Clear()
        // raises a Reset that drops the ListBox's SelectedItem to null, which two-way binds
        // back into SelectedEnvironment and empty-states the detail pane.
        if (e.Action == NotifyCollectionChangedAction.Replace
            && string.IsNullOrWhiteSpace(SearchText)
            && e.OldItems is not null && e.NewItems is not null
            && e.OldItems.Count == e.NewItems.Count)
        {
            for (var i = 0; i < e.OldItems.Count; i++)
            {
                if (e.OldItems[i] is not DomainEnv oldItem) continue;
                if (e.NewItems[i] is not DomainEnv newItem) continue;
                var idx = Filtered.IndexOf(oldItem);
                if (idx >= 0) Filtered[idx] = newItem;
            }
            TryRestorePendingSelection();
            return;
        }
        RebuildFiltered();
        TryRestorePendingSelection();
    }

    /// <summary>Looks up <see cref="_pendingRestoreId"/> / <see cref="_pendingRestoreName"/>
    /// in the rebuilt <see cref="Filtered"/> and re-pins <see cref="SelectedEnvironment"/>
    /// when the matching env reappears. Clears the pending state after a successful
    /// restore so a later genuine deselect (e.g. Delete) isn't undone.</summary>
    private void TryRestorePendingSelection()
    {
        if (string.IsNullOrEmpty(_pendingRestoreId) && string.IsNullOrEmpty(_pendingRestoreName))
            return;
        if (SelectedEnvironment is not null
            && (string.Equals(SelectedEnvironment.Id, _pendingRestoreId, StringComparison.Ordinal)
                || string.Equals(SelectedEnvironment.Name, _pendingRestoreName, StringComparison.OrdinalIgnoreCase)))
        {
            // Already on the right env — drop the pending state.
            _pendingRestoreId = null;
            _pendingRestoreName = null;
            return;
        }

        var match = (!string.IsNullOrEmpty(_pendingRestoreId)
            ? Filtered.FirstOrDefault(e => string.Equals(e.Id, _pendingRestoreId, StringComparison.Ordinal))
            : null)
            ?? (!string.IsNullOrEmpty(_pendingRestoreName)
                ? Filtered.FirstOrDefault(e => string.Equals(e.Name, _pendingRestoreName, StringComparison.OrdinalIgnoreCase))
                : null);
        if (match is not null)
        {
            SelectedEnvironment = match;
            _pendingRestoreId = null;
            _pendingRestoreName = null;
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildFiltered();

    /// <summary>The on-disk root for collection-scoped env files. We persist into the active
    /// collection's <c>environments/</c> folder, not the workspace's, because this panel
    /// shows collection envs only. Earlier code wrote to <c>workspace.FolderPath</c> instead —
    /// the file landed in <c>&lt;workspace&gt;/environments/</c>, the in-memory list still
    /// pointed at <see cref="CollectionsViewModel.CollectionEnvironments"/>, and on the next
    /// workspace switch the loader picked the file up as a workspace env. That was the
    /// "imported env jumps from collection to workspace level after reload" bug.</summary>
    private string? CollectionEnvRoot => _collections.ActiveCollection?.SourcePath;

    private void RebuildFiltered()
    {
        // Snapshot the current selection's identity. After Filtered.Clear() the ListBox's
        // SelectedItem is nulled (Reset event), which two-way-binds null back into
        // SelectedEnvironment and empty-states the detail pane — that's the
        // "Save under collection → blank screen" the user kept seeing. The targeted Replace
        // path in OnAllCollectionChanged covers the in-process swap, but the file watcher
        // can still trigger a full rebuild (Clear + Add) on the way back through
        // CollectionsViewModel.RebuildEnvironments. Re-pin selection by Id (preferred —
        // stable across rename) and fall back to Name for back-compat with envs whose
        // Id field was empty in older state.
        var keepId = SelectedEnvironment?.Id;
        var keepName = SelectedEnvironment?.Name;

        Filtered.Clear();
        var query = (SearchText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            foreach (var env in All) Filtered.Add(env);
        }
        else
        {
            foreach (var env in All)
            {
                if (env.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    Filtered.Add(env);
            }
        }

        if (!string.IsNullOrEmpty(keepId) || !string.IsNullOrEmpty(keepName))
        {
            var match = (!string.IsNullOrEmpty(keepId)
                ? Filtered.FirstOrDefault(e => string.Equals(e.Id, keepId, StringComparison.Ordinal))
                : null)
                ?? (!string.IsNullOrEmpty(keepName)
                    ? Filtered.FirstOrDefault(e => string.Equals(e.Name, keepName, StringComparison.OrdinalIgnoreCase))
                    : null);
            if (match is not null && !ReferenceEquals(SelectedEnvironment, match))
                SelectedEnvironment = match;
        }
    }

    private void OnCollectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionsViewModel.ActiveEnvironment))
        {
            OnPropertyChanged(nameof(Active));
            Refresh();
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        StatusMessage = Active is null
            ? "No environment selected."
            : $"Active: {Active.Name} ({Active.Variables.Count} variable(s)).";
    }

    [RelayCommand]
    private void Activate(DomainEnv? env)
    {
        if (env is null) return;
        _collections.ActiveEnvironment = env;
    }

    /// <summary>Raised when the user picks the edit icon on an env row. The host opens
    /// an EnvironmentTabViewModel in the workspace area; saves flow through
    /// <see cref="SaveEnvironmentAsync"/>.</summary>
    public event EventHandler<DomainEnv>? EditRequested;

    [RelayCommand]
    private void Edit(DomainEnv? env)
    {
        if (env is null) return;
        EditRequested?.Invoke(this, env);
    }

    [RelayCommand]
    private void CreateEnvironment()
    {
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) { StatusMessage = "Activate a collection first."; return; }

        var name = ResolveUniqueName("new-env");
        var env = new DomainEnv { Id = Guid.NewGuid().ToString("N"), Name = name };
        AddToCollectionLists(env);
        if (_collections.ActiveEnvironment is null) _collections.ActiveEnvironment = env;
        // Drive the master/detail to the newly-created env so the user lands directly in
        // the variable editor — the previous flow opened a separate tab.
        SelectedEnvironment = env;

        try
        {
            WriteSingleEnvFile(root, env);
            StatusMessage = $"Created “{name}”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create env failed");
            StatusMessage = $"Create failed: {ex.Message}";
        }
    }

    /// <summary>Inserts an env into the two lists the UI binds against — the collection-scoped
    /// list (left toolbar panel) and the back-compat union (older consumers). Without the
    /// dual-write the panel and the top-bar's "Collection" tab would disagree about which
    /// envs exist.</summary>
    private void AddToCollectionLists(DomainEnv env)
    {
        _collections.CollectionEnvironments.Add(env);
        if (!_collections.Environments.Contains(env)) _collections.Environments.Add(env);
    }

    /// <summary>Persists a single env edit (called by EnvironmentTabViewModel on Save). Writes
    /// only the env file so unrelated collection state — including folders/requests with
    /// names containing characters that are valid in Bruno but not in OS paths (e.g. <c>*</c>) —
    /// stays untouched. Routes the in-memory swap through
    /// <see cref="CollectionsViewModel.ReplaceEnvironment"/> so the top-bar pill and every
    /// list binding refresh together.</summary>
    public Task SaveEnvironmentAsync(DomainEnv updated)
    {
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) return Task.CompletedTask;

        var match = _collections.CollectionEnvironments.FirstOrDefault(e =>
                       string.Equals(e.Name, updated.Name, StringComparison.OrdinalIgnoreCase))
                 ?? _collections.GlobalEnvironments.FirstOrDefault(e =>
                       string.Equals(e.Name, updated.Name, StringComparison.OrdinalIgnoreCase))
                 ?? _collections.Environments.FirstOrDefault(e =>
                       string.Equals(e.Name, updated.Name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            // Capture selection match BEFORE the replace — the ListBox in the master pane
            // drops SelectedItem to null when its bound list's item is swapped, so a post-
            // swap check sees the wrong (null) state and the detail pane empty-states.
            var wasSelected = ReferenceEquals(SelectedEnvironment, match);
            _collections.ReplaceEnvironment(match, updated);
            if (wasSelected) SelectedEnvironment = updated;
        }

        try { WriteSingleEnvFile(root, updated); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save env failed");
            StatusMessage = $"Save failed: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    /// <summary>Raised when the panel wants the host to prompt for a new name. Code-behind
    /// opens <c>RenameDialog</c> and calls <see cref="RenameEnvironmentAsync"/> with the result;
    /// this event keeps the dialog out of the VM (the VM still owns the disk + state changes).</summary>
    public event EventHandler<DomainEnv>? RenameRequested;

    [RelayCommand]
    private void RequestRename(DomainEnv? env)
    {
        if (env is null) return;
        RenameRequested?.Invoke(this, env);
    }

    /// <summary>Renames an env on disk + in memory. Sanitizes both the old and new file names
    /// so a name with characters invalid for OS paths still resolves to the same file
    /// <see cref="WriteSingleEnvFile"/> originally wrote. Without sanitization the old file
    /// can't be found and stays on disk — that's the "rename made copies with new names" bug.
    /// On success, drives the in-memory swap through <see cref="CollectionsViewModel.ReplaceEnvironment"/>
    /// so the rename appears in the top-bar pill, the panel, and any other binding at once.</summary>
    public Task RenameEnvironmentAsync(DomainEnv env, string newName)
    {
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) { StatusMessage = "Activate a collection first."; return Task.CompletedTask; }
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, env.Name, StringComparison.Ordinal))
            return Task.CompletedTask;

        try
        {
            var envDir = Path.Combine(root, CollectionJson.EnvironmentsFolder);
            Directory.CreateDirectory(envDir);
            var oldPath = Path.Combine(envDir, SanitizeFileName(env.Name) + CollectionJson.EnvironmentSuffix);
            var newPath = Path.Combine(envDir, SanitizeFileName(newName) + CollectionJson.EnvironmentSuffix);

            var renamed = env with { Name = newName };
            WriteSingleEnvFile(root, renamed);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                File.Delete(oldPath);

            var wasSelected = ReferenceEquals(SelectedEnvironment, env);
            _collections.ReplaceEnvironment(env, renamed);
            if (wasSelected) SelectedEnvironment = renamed;
            StatusMessage = $"Renamed to “{newName}”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename env failed");
            StatusMessage = $"Rename failed: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    /// <summary>Writes one env file into <c>&lt;root&gt;/environments/&lt;sanitized&gt;.env.json</c>.
    /// Bypasses CollectionStore.Save so we don't iterate the whole tree (and don't choke on
    /// folder/request names with characters that are invalid for Path APIs).</summary>
    private static void WriteSingleEnvFile(string root, DomainEnv env)
    {
        var envDir = Path.Combine(root, Vegha.Core.FileFormat.CollectionJson.EnvironmentsFolder);
        Directory.CreateDirectory(envDir);
        var fileName = SanitizeFileName(env.Name) + Vegha.Core.FileFormat.CollectionJson.EnvironmentSuffix;
        // Move literal secret values into the encrypted sidecar so the .env.json on disk
        // never carries them. The caller's in-memory env keeps full values.
        var store = new Vegha.Core.Persistence.EnvironmentSecretStore();
        var stripped = Vegha.Core.FileFormat.EnvironmentSecretSplitter.StripForPersistence(env, root, store);
        var envFile = ToEnvironmentFile(stripped);
        File.WriteAllText(
            Path.Combine(envDir, fileName),
            Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(envFile));
    }

    private static Vegha.Core.FileFormat.EnvironmentFile ToEnvironmentFile(DomainEnv env) =>
        new()
        {
            // Persist the stable Id — the encrypted secret sidecar is keyed by it. Omitting
            // it makes the loader mint a fresh Id on every reload, orphaning the sidecar so
            // secret values come back empty.
            Id = env.Id,
            Name = env.Name,
            Variables = env.Variables.Select(Vegha.Core.FileFormat.KvDto.FromDomain).ToList(),
            SecretVariables = env.SecretVariables.ToList(),
            Color = env.Color,
        };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    // ----- Import / Export / Copy / Delete / SetColor -----

    /// <summary>Imports environment files (Postman or Bruno-style JSON) into the active
    /// workspace. Each path is run through <see cref="ImportPipeline.DetectAndImportPath"/>;
    /// non-environment results are skipped with a status warning so the user knows the file
    /// was a collection, not an env. Returns the count successfully imported.</summary>
    public int ImportEnvironments(IEnumerable<string> paths)
    {
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) { StatusMessage = "Activate a collection first."; return 0; }

        var imported = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                var result = ImportPipeline.DetectAndImportPath(path);
                if (!result.Success || result.Environment is null)
                {
                    skipped++;
                    continue;
                }
                var env = result.Environment;
                var name = ResolveUniqueName(env.Name);
                if (!string.Equals(name, env.Name, StringComparison.Ordinal))
                    env = env with { Name = name };

                AddToCollectionLists(env);
                if (_collections.ActiveEnvironment is null) _collections.ActiveEnvironment = env;
                WriteSingleEnvFile(root, env);
                imported++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import env failed for {Path}", path);
                skipped++;
            }
        }

        StatusMessage = skipped == 0
            ? $"Imported {imported} environment(s)."
            : $"Imported {imported}, skipped {skipped} (not an environment file).";
        return imported;
    }

    /// <summary>Serializes an env to disk at the user-chosen path. The .env.json format is
    /// the canonical on-disk shape; the dialog's file picker should default the extension.</summary>
    public void ExportEnvironment(DomainEnv env, string destinationPath)
    {
        var file = ToEnvironmentFile(env);
        File.WriteAllText(destinationPath, CollectionJson.SerializeEnvironment(file));
        StatusMessage = $"Exported “{env.Name}” to {Path.GetFileName(destinationPath)}.";
    }

    [RelayCommand]
    private void CopyEnvironment(DomainEnv? env)
    {
        if (env is null) return;
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) { StatusMessage = "Activate a collection first."; return; }

        var newName = ResolveUniqueName(env.Name + " copy");
        var copy = env with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = newName,
            Variables = env.Variables.Select(v => v with { }).ToList(),
            SecretVariables = env.SecretVariables.ToList(),
        };

        try
        {
            AddToCollectionLists(copy);
            WriteSingleEnvFile(root, copy);
            SelectedEnvironment = copy;
            StatusMessage = $"Copied to “{newName}”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy env failed");
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteEnvironment(DomainEnv? env)
    {
        if (env is null) return;
        var root = CollectionEnvRoot;
        if (string.IsNullOrEmpty(root)) { StatusMessage = "Activate a collection first."; return; }

        try
        {
            var envDir = Path.Combine(root, CollectionJson.EnvironmentsFolder);
            var fileName = SanitizeFileName(env.Name) + CollectionJson.EnvironmentSuffix;
            var fullPath = Path.Combine(envDir, fileName);
            if (File.Exists(fullPath)) File.Delete(fullPath);
            new Vegha.Core.Persistence.EnvironmentSecretStore().Delete(root, env.Id);

            _collections.CollectionEnvironments.Remove(env);
            _collections.Environments.Remove(env);
            if (ReferenceEquals(_collections.ActiveEnvironment, env))
                _collections.ActiveEnvironment = null;

            StatusMessage = $"Deleted “{env.Name}”.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete env failed");
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>Applies a new color to an env and persists it. Pass null/empty hex to clear
    /// the color (fall back to neutral styling in the UI).</summary>
    public Task SetColorAsync(DomainEnv env, string? hex)
    {
        var updated = env with { Color = string.IsNullOrWhiteSpace(hex) ? null : hex };
        return SaveEnvironmentAsync(updated);
    }

    private string ResolveUniqueName(string proposed)
    {
        var existing = new HashSet<string>(
            _collections.CollectionEnvironments.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(proposed)) return proposed;
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{proposed} {n}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return proposed + " " + Guid.NewGuid().ToString("N")[..6];
    }

}

public partial class EnvVarRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isSecret;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>True while the user has clicked the eye toggle to reveal a secret value.
    /// Resets to false (re-masked) whenever the secret flag is toggled.</summary>
    [ObservableProperty] private bool _isRevealed;

    /// <summary>Drives the value TextBox's <c>PasswordChar</c> via
    /// <c>BoolToPasswordCharConverter</c>: a non-secret row is always visible; a secret
    /// row is visible only while explicitly revealed.</summary>
    public bool IsValueVisible => !IsSecret || IsRevealed;

    /// <summary>The eye toggle only matters for secret rows — hidden otherwise.</summary>
    public bool CanRevealToggle => IsSecret;

    partial void OnIsSecretChanged(bool value)
    {
        IsRevealed = false;
        OnPropertyChanged(nameof(IsValueVisible));
        OnPropertyChanged(nameof(CanRevealToggle));
    }

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(IsValueVisible));

    // --- Secret-manager binding picker ------------------------------------------------
    // Names of secret providers registered in the SecretRegistry. Shared list reference
    // set by the hosting ViewModel at hydration time so the row's picker dropdown can be
    // bound directly (a Flyout's content lives in a detached popup tree, so ancestor
    // bindings to the panel don't resolve — the row must carry the list itself).
    [ObservableProperty] private IReadOnlyList<string> _providerNames = Array.Empty<string>();
    [ObservableProperty] private string? _providerName;
    [ObservableProperty] private string _providerPath = string.Empty;
    [ObservableProperty] private string _providerField = string.Empty;

    /// <summary>Composes the picker selections into a <c>secret://provider/path#field</c>
    /// URI and writes it as the variable value. The Interpolator resolves it at request
    /// time. Reveals the value so the user can see the reference they just bound.</summary>
    [RelayCommand]
    private void ApplyProviderBinding()
    {
        if (string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(ProviderPath))
            return;
        var field = string.IsNullOrWhiteSpace(ProviderField) ? string.Empty : "#" + ProviderField.Trim();
        Value = $"secret://{ProviderName.Trim()}/{ProviderPath.Trim()}{field}";
        IsRevealed = true;
    }
}
