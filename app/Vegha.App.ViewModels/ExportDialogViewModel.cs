using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.FileFormat;
using Vegha.Core.Importers;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Export dialog: pick a scope (whole workspace, or a checklist of collections),
/// a format, and options; <see cref="BuildAsync"/> runs <see cref="ExportService"/> off the UI
/// thread and returns the packaged file. The dialog then asks for a save location with the
/// result's suggested name (the extension depends on the outcome — e.g. Postman becomes a zip
/// only when environments are included), writes it, and shows any lossy-mapping warnings.
/// </summary>
public partial class ExportDialogViewModel : ObservableObject
{
    public ObservableCollection<ExportCollectionChoice> Collections { get; } = new();

    public IReadOnlyList<ExportFormatInfo> Formats => ExportFormatInfo.All;

    /// <summary>Lossy-mapping notes from the last export. Non-empty keeps the dialog open
    /// after saving so the user can read them.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenApi), nameof(SupportsEnvironments), nameof(FidelityNote))]
    private ExportFormatInfo _selectedFormat = ExportFormatInfo.All[0];

    /// <summary>True = entire workspace (all collections + workspace envs/scripts);
    /// false = only the checked collections.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(IsSelectionScope))]
    private bool _isWorkspaceScope;

    [ObservableProperty]
    private bool _includeEnvironments = true;

    [ObservableProperty]
    private bool _includeSecrets;

    [ObservableProperty]
    private bool _openApiYaml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Inverse of <see cref="IsWorkspaceScope"/> — settable so the dialog's second
    /// radio button can bind two-way.</summary>
    public bool IsSelectionScope
    {
        get => !IsWorkspaceScope;
        set => IsWorkspaceScope = !value;
    }

    public bool HasWarnings => Warnings.Count > 0;
    public bool IsOpenApi => SelectedFormat.Format == ExportFormat.OpenApi30;
    public bool SupportsEnvironments => !IsOpenApi;
    public string FidelityNote => SelectedFormat.FidelityNote;

    public bool CanExport => !IsBusy && (IsWorkspaceScope ? Collections.Count > 0 : Collections.Any(c => c.IsSelected));

    public ExportDialogViewModel()
    {
        Warnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWarnings));
    }

    /// <summary>Active workspace name/folder — null when no workspace is active, in which
    /// case the workspace scope is unavailable.</summary>
    public string? WorkspaceName { get; private set; }
    public string? WorkspaceFolder { get; private set; }
    public bool HasWorkspace => !string.IsNullOrEmpty(WorkspaceFolder);

    /// <summary>Seeds the checklist. <paramref name="collections"/> should be every collection
    /// in the workspace (not just the open ones) — the workspace scope exports all of them.
    /// With <paramref name="preselectPath"/> (right-click → Export…) only that collection is
    /// checked and the scope starts at "Selected collections"; otherwise (File → Export…)
    /// everything is checked and the workspace scope is the default.</summary>
    public void Initialize(
        IEnumerable<(string Name, string SourcePath, Collection? Collection)> collections,
        string? workspaceName, string? workspaceFolder, string? preselectPath = null)
    {
        WorkspaceName = workspaceName;
        WorkspaceFolder = workspaceFolder;
        Collections.Clear();
        foreach (var (name, sourcePath, collection) in collections)
        {
            var choice = new ExportCollectionChoice(name, sourcePath, collection)
            {
                IsSelected = preselectPath is null || string.Equals(sourcePath, preselectPath, StringComparison.OrdinalIgnoreCase),
            };
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportCollectionChoice.IsSelected)) OnPropertyChanged(nameof(CanExport));
            };
            Collections.Add(choice);
        }
        IsWorkspaceScope = preselectPath is null && HasWorkspace;
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(CanExport));
    }

    public ExportOptions BuildOptions() => new(
        IncludeEnvironments: IncludeEnvironments && SupportsEnvironments,
        IncludeSecrets: IncludeSecrets,
        OpenApiYaml: OpenApiYaml);

    /// <summary>Runs the export off the UI thread. Collections are re-read from disk so the
    /// export reflects what's saved (the in-memory root can lag behind watcher reloads);
    /// in-memory roots without a folder fall back to their loaded model.</summary>
    public async Task<ExportResult?> BuildAsync()
    {
        if (!CanExport) return null;
        IsBusy = true;
        StatusMessage = null;
        Warnings.Clear();
        try
        {
            var chosen = (IsWorkspaceScope ? Collections : Collections.Where(c => c.IsSelected)).ToList();
            var format = SelectedFormat.Format;
            var options = BuildOptions();
            var workspace = IsWorkspaceScope && HasWorkspace
                ? new ExportWorkspace(WorkspaceName ?? "workspace", WorkspaceModel.Empty)
                : null;
            var folder = WorkspaceFolder;

            return await Task.Run(() =>
            {
                var sources = chosen.Select(c => new ExportSource(LoadCollection(c), c.SourcePath)).ToList();
                var ws = workspace is null ? null : workspace with { Model = WorkspaceModelLoader.Load(folder!) };
                return ExportService.Export(sources, format, options, ws);
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes <paramref name="result"/> to <paramref name="destination"/> and records
    /// the outcome. Returns false (with <see cref="StatusMessage"/> set) on I/O failure.</summary>
    public async Task<bool> SaveAsync(ExportResult result, Stream destination, string displayPath)
    {
        try
        {
            await destination.WriteAsync(result.Content);
            foreach (var w in result.Warnings) Warnings.Add(w);
            StatusMessage = SummaryFor(result, displayPath);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not write {displayPath}: {ex.Message}";
            return false;
        }
    }

    public static string SummaryFor(ExportResult result, string displayPath) =>
        $"Exported {result.CollectionCount} collection{(result.CollectionCount == 1 ? "" : "s")} to {displayPath}.";

    private static Collection LoadCollection(ExportCollectionChoice c)
    {
        if (!string.IsNullOrEmpty(c.SourcePath) && Directory.Exists(c.SourcePath))
            return CollectionLoader.Load(c.SourcePath);
        return c.Collection ?? new Collection { Name = c.Name };
    }
}

/// <summary>One row of the Export dialog's collection checklist.</summary>
public sealed partial class ExportCollectionChoice(string name, string sourcePath, Collection? collection) : ObservableObject
{
    public string Name { get; } = name;
    public string SourcePath { get; } = sourcePath;
    public Collection? Collection { get; } = collection;

    [ObservableProperty]
    private bool _isSelected;
}
