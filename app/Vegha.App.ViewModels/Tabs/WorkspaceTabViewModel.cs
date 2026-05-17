using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.FileFormat;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Main-area tab that hosts the workspace editor: an "Overview" sub-tab with stats + the
/// collection list, and an "Environments" sub-tab with the workspace-level env editor.
/// Reuses the existing <see cref="EnvVarRow"/> grid for env editing — same affordances the
/// per-collection env tab uses.
/// </summary>
public sealed partial class WorkspaceTabViewModel : RequestTabViewModel
{
    /// <summary>Workspace this tab edits — named <c>WorkspaceItem</c> to avoid colliding with
    /// the base <see cref="RequestTabViewModel.Workspace"/> abstract member (which is the
    /// per-kind workspace ViewModel the workspace area's ContentControl binds to).</summary>
    public WorkspaceItemViewModel WorkspaceItem { get; }

    /// <summary>Read-only mirror of the workspace's collections, refreshed when the host
    /// reloads them (workspace switch / add / remove). The Overview tab binds to this.</summary>
    public ObservableCollection<CollectionRootViewModel> Collections { get; } = new();

    /// <summary>Workspace-level envs loaded from <c>&lt;workspace&gt;/environments/*.env.json</c>.</summary>
    public ObservableCollection<DomainEnv> Environments { get; } = new();

    [ObservableProperty]
    private DomainEnv? _selectedEnvironment;

    /// <summary>Variable rows of the currently-selected env. The Environments sub-tab binds here.</summary>
    public ObservableCollection<EnvVarRow> Variables { get; } = new();

    [ObservableProperty] private string? _statusMessage;

    /// <summary>"overview" or "environments" — the sub-tab selection (TabControl SelectedIndex
    /// could be used too, but a string switch is friendlier to bind across multiple controls).</summary>
    [ObservableProperty]
    private string _activeSection = "overview";

    public int CollectionsCount => Collections.Count;
    public int EnvironmentsCount => Environments.Count;

    /// <summary>Host callback that opens or imports a collection — invoked by the Overview quick actions.
    /// Set by the host (MainWindow) after construction so the tab itself doesn't depend on dialogs.</summary>
    public Action? RequestCreateCollection { get; set; }
    public Action? RequestOpenCollection { get; set; }
    public Action? RequestImportCollection { get; set; }

    /// <summary>Activates the picked collection in the main window (sidebar + scope flips).</summary>
    public Action<CollectionRootViewModel>? ActivateCollection { get; set; }

    // ---- Workspace-level actions (header "…" menu) ----
    public Action? RequestRenameWorkspace { get; set; }
    public Action? RequestRevealWorkspaceInExplorer { get; set; }
    public Action? RequestExportWorkspace { get; set; }
    public Action? RequestCloseWorkspace { get; set; }

    // ---- Per-collection actions (Overview row menus) ----
    public Action<CollectionRootViewModel>? RequestRenameCollection { get; set; }
    public Action<CollectionRootViewModel>? RequestRemoveCollection { get; set; }
    public Action<CollectionRootViewModel>? RequestDeleteCollection { get; set; }

    // ---- Per-env actions (env editor header) ----
    public Action<DomainEnv>? RequestRenameEnvironment { get; set; }
    public Action<DomainEnv>? RequestCopyEnvironment { get; set; }
    public Action<DomainEnv>? RequestDeleteEnvironment { get; set; }
    public Action<DomainEnv>? RequestSetEnvColor { get; set; }
    public Action? RequestImportEnvironment { get; set; }

    /// <summary>Host hook fired when the user changes the selected env in the dialog's list.
    /// MainWindow uses this to flip <c>CollectionsViewModel.ActiveGlobalEnvironment</c> so
    /// the top-bar pill, the env picker, and the per-workspace persistence all follow the
    /// dialog selection. Without this the dialog's selection was visual-only and the
    /// workspace env never actually activated.</summary>
    public Action<DomainEnv?>? RequestActivateEnvironment { get; set; }

    /// <summary>Fires after a variable Save completes so the host can mirror the new env into
    /// the shared <see cref="CollectionsViewModel"/> state (top-bar pill, env picker, panel).
    /// Without this hook the dialog's local list was the only thing that updated.</summary>
    public event EventHandler<(DomainEnv Old, DomainEnv New)>? EnvironmentSaved;

    /// <summary>The per-kind workspace ViewModel the ContentControl binds to — for this tab,
    /// the tab itself is the ViewModel (the editor view picks its sections off it).</summary>
    public override object Workspace => this;

    public WorkspaceTabViewModel(WorkspaceItemViewModel workspace, string id)
    {
        WorkspaceItem = workspace;
        Id = id;
        Name = workspace.Name;
        Method = "WS";
        Kind = Vegha.Core.Domain.RequestKind.Http; // any kind — the tab strip uses Method label
        Collections.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CollectionsCount));
        };
        Environments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(EnvironmentsCount));
        };
    }

    /// <summary>Replaces the in-memory env list (host reloads on workspace activate). The
    /// previously-selected env is restored by name when possible so the editor doesn't blink
    /// blank during refresh. <paramref name="initialSelection"/> wins when provided and
    /// present in the new list — the host passes the current active workspace env so the
    /// dialog opens already-aligned with what the top-bar pill shows.</summary>
    public void SetEnvironments(IEnumerable<DomainEnv> envs, DomainEnv? initialSelection = null)
    {
        var previousName = SelectedEnvironment?.Name;
        Environments.Clear();
        foreach (var e in envs) Environments.Add(e);

        if (initialSelection is not null && Environments.Contains(initialSelection))
        {
            SelectedEnvironment = initialSelection;
            return;
        }
        SelectedEnvironment = string.IsNullOrEmpty(previousName)
            ? Environments.FirstOrDefault()
            : Environments.FirstOrDefault(e =>
                  string.Equals(e.Name, previousName, StringComparison.OrdinalIgnoreCase))
              ?? Environments.FirstOrDefault();
    }

    /// <summary>Backing reference to the source list (the shared <c>AvailableCollections</c>).
    /// Kept so we can unsubscribe in <see cref="SetCollections"/> reassignments and so the
    /// mirror stays in sync as the source mutates.</summary>
    private System.Collections.Specialized.INotifyCollectionChanged? _collectionsSource;

    public void SetCollections(IEnumerable<CollectionRootViewModel> collections)
    {
        // Unsubscribe from the previous source so a re-Set on workspace switch doesn't keep
        // mirroring stale changes.
        if (_collectionsSource is not null)
            _collectionsSource.CollectionChanged -= OnSourceCollectionsChanged;

        Collections.Clear();
        foreach (var c in collections) Collections.Add(c);

        // When the source list is observable (it is — AvailableCollections), mirror future
        // mutations into Collections so Add / Remove / Replace performed outside the dialog
        // (e.g. Delete collection from the workspace editor's row menu, Import collection
        // from Quick Actions) refresh the list view in real time. Without this the dialog
        // showed the stale snapshot until the user closed and reopened it.
        if (collections is System.Collections.Specialized.INotifyCollectionChanged notify)
        {
            _collectionsSource = notify;
            notify.CollectionChanged += OnSourceCollectionsChanged;
        }
        else
        {
            _collectionsSource = null;
        }
    }

    private void OnSourceCollectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Rebuild rather than try to translate Add/Remove indexes — Avalonia's ListBox handles
        // bulk Reset on a small collection just fine, and Roots.IndexOf semantics under
        // Replace would otherwise need careful mirroring.
        if (sender is not IEnumerable<CollectionRootViewModel> live) return;
        Collections.Clear();
        foreach (var c in live) Collections.Add(c);
    }

    partial void OnSelectedEnvironmentChanged(DomainEnv? value)
    {
        HydrateVariables();
        RequestActivateEnvironment?.Invoke(value);
    }

    private void HydrateVariables()
    {
        Variables.Clear();
        if (SelectedEnvironment is null) return;
        var secrets = new HashSet<string>(SelectedEnvironment.SecretVariables, StringComparer.Ordinal);
        foreach (var v in SelectedEnvironment.Variables)
        {
            Variables.Add(new EnvVarRow
            {
                Name = v.Name,
                Value = v.Value,
                IsSecret = secrets.Contains(v.Name),
                IsEnabled = v.Enabled,
            });
        }
        IsDirty = false;
    }

    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(new EnvVarRow { Name = string.Empty, Value = string.Empty, IsEnabled = true });
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveVariable(EnvVarRow? row)
    {
        if (row is null) return;
        Variables.Remove(row);
        IsDirty = true;
    }

    /// <summary>Reset re-hydrates the editor from the in-memory env, discarding unsaved edits.</summary>
    [RelayCommand]
    private void Reset()
    {
        HydrateVariables();
        StatusMessage = "Reverted unsaved changes.";
    }

    /// <summary>Save persists the env back to <c>&lt;workspace&gt;/environments/&lt;name&gt;.env.json</c>
    /// and updates the in-memory list so subsequent edits start from the saved state.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedEnvironment is null) { StatusMessage = "No environment selected."; return; }
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
            var updated = SelectedEnvironment with { Variables = newVars, SecretVariables = newSecrets };

            var dir = System.IO.Path.Combine(WorkspaceItem.FolderPath, WorkspaceModelLoader.EnvironmentsFolder);
            System.IO.Directory.CreateDirectory(dir);
            // Sanitize the filename so envs whose names contain characters invalid for Path
            // APIs still resolve to a real file (and the rename/delete paths can find them).
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string(updated.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (string.IsNullOrEmpty(safe)) safe = "untitled";
            var path = System.IO.Path.Combine(dir, safe + CollectionJson.EnvironmentSuffix);
            var json = CollectionJson.SerializeEnvironment(EnvironmentFile.FromDomain(updated));
            await System.IO.File.WriteAllTextAsync(path, json);

            // Update in-memory model so the workspace's env merge picks up the change without a reload.
            var previous = SelectedEnvironment;
            var idx = Environments.IndexOf(SelectedEnvironment);
            if (idx >= 0) Environments[idx] = updated;
            SelectedEnvironment = updated;
            IsDirty = false;
            StatusMessage = $"Saved “{updated.Name}”.";
            EnvironmentSaved?.Invoke(this, (previous, updated));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }
}
