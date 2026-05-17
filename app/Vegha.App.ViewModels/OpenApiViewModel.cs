using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the OpenAPI sidebar. The Sync command fetches a spec (URL or file) and produces
/// a drift report against the active collection. Three apply paths then let the user
/// act on that drift:
///
/// 1. <see cref="ImportAddedOpsCommand"/> — adds operations the spec introduced into a
///    new sub-folder of the active collection.
/// 2. <see cref="DeleteRemovedOpsCommand"/> — removes requests whose corresponding
///    operation no longer exists in the spec.
/// 3. <see cref="ReplaceCollectionCommand"/> — wipes the collection and rewrites it from
///    the spec (auxiliary files like environments/ are preserved).
///
/// <see cref="CreateCollectionFromSpecCommand"/> handles the no-collection-loaded path —
/// writes a fresh collection under the active workspace's <c>collections/</c> folder.
///
/// Clicking a drift row navigates to the matching request when one exists (Unchanged /
/// RemovedFromSpec). Disk mutations all funnel through <see cref="OpenApiSpecApplier"/>
/// so the same code is exercised by unit tests.
/// </summary>
public partial class OpenApiViewModel : ObservableObject
{
    private readonly CollectionsViewModel _collections;
    private readonly WorkspacesViewModel? _workspaces;
    private readonly ILogger<OpenApiViewModel> _logger;

    [ObservableProperty]
    private string _specSource = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<DriftRow> AddedInSpec { get; } = new();
    public ObservableCollection<DriftRow> RemovedFromSpec { get; } = new();
    public ObservableCollection<DriftRow> Unchanged { get; } = new();

    [ObservableProperty]
    private string? _lastSyncedSource;

    [ObservableProperty]
    private string? _lastSyncedAt;

    /// <summary>True when a workspace collection is currently loaded — used to gate
    /// the apply-drift buttons (which only make sense against an existing collection)
    /// and to surface the alternative quick-import path when no collection is loaded.</summary>
    [ObservableProperty]
    private bool _hasActiveCollection;

    /// <summary>Cached spec from the last successful Sync. Apply commands replay against
    /// this rather than re-fetching, so URL flakiness doesn't break the apply path.</summary>
    private Collection? _lastSpec;

    public OpenApiViewModel(
        CollectionsViewModel collections,
        ILogger<OpenApiViewModel> logger,
        WorkspacesViewModel? workspaces = null)
    {
        _collections = collections;
        _workspaces = workspaces;
        _logger = logger;

        RecomputeActiveCollection();
        _collections.PropertyChanged += OnCollectionsPropertyChanged;
    }

    private void OnCollectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionsViewModel.ActiveCollection))
            RecomputeActiveCollection();
    }

    private void RecomputeActiveCollection() =>
        HasActiveCollection = _collections.ActiveCollection?.Collection is not null;

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (string.IsNullOrWhiteSpace(SpecSource))
        {
            StatusMessage = "Enter a URL or file path.";
            return;
        }

        try
        {
            var content = await LoadSpecAsync(SpecSource).ConfigureAwait(true);
            var liveSpec = OpenApiImporter.ImportFromString(content);
            _lastSpec = liveSpec;

            var userCollection = ActiveCollectionAsModel();
            if (userCollection is null)
            {
                ShowAsAdded(liveSpec);
                return;
            }

            var entries = OpenApiDriftDetector.Compare(userCollection, liveSpec);
            AddedInSpec.Clear(); RemovedFromSpec.Clear(); Unchanged.Clear();
            foreach (var e in entries)
            {
                var row = new DriftRow(e.Method, e.Path, e.RequestName ?? string.Empty);
                switch (e.Kind)
                {
                    case OpenApiDriftDetector.DriftKind.AddedInSpec: AddedInSpec.Add(row); break;
                    case OpenApiDriftDetector.DriftKind.RemovedFromSpec: RemovedFromSpec.Add(row); break;
                    case OpenApiDriftDetector.DriftKind.Unchanged: Unchanged.Add(row); break;
                }
            }
            LastSyncedSource = SpecSource;
            LastSyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            StatusMessage = $"Drift: +{AddedInSpec.Count} new · −{RemovedFromSpec.Count} removed · {Unchanged.Count} unchanged.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAPI sync failed");
            StatusMessage = $"Sync failed: {ex.Message}";
        }
    }

    /// <summary>Click handler for a drift row — navigates to the matching request in
    /// the workspace tree. Rows under "Added in spec" have no corresponding request,
    /// so they no-op (the user must Import first).</summary>
    [RelayCommand]
    private void OpenRow(DriftRow? row)
    {
        if (row is null) return;
        var match = FindRequestItem(row.Method, row.Path);
        if (match is null)
        {
            StatusMessage = "Request not found in the active collection — try Import added or Sync.";
            return;
        }
        // OpenRequest is a private [RelayCommand] on CollectionsViewModel; the generated
        // ICommand surface is how we trigger it from outside.
        _collections.OpenRequestCommand.Execute(match);
    }

    /// <summary>Adds operations the spec introduced into a new sub-folder of the active
    /// collection. Existing files are untouched. Quietly no-ops when nothing's added.</summary>
    [RelayCommand]
    private void ImportAddedOps()
    {
        var root = _collections.ActiveCollection;
        if (root?.Collection is null || string.IsNullOrEmpty(root.SourcePath))
        {
            StatusMessage = "No active collection to import into.";
            return;
        }
        if (_lastSpec is null) { StatusMessage = "Run Sync first."; return; }

        var newOps = ResolveLiveRequests(AddedInSpec);
        if (newOps.Count == 0) { StatusMessage = "Nothing to import."; return; }

        try
        {
            var folderName = $"New from spec {DateTime.Now:yyyy-MM-dd HH-mm}";
            OpenApiSpecApplier.WriteAddedFolder(root.SourcePath, newOps, folderName);
            _collections.LoadFromDirectory(root.SourcePath);
            StatusMessage = $"Imported {newOps.Count} operation(s) into '{folderName}'. Re-syncing…";
            _ = SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import added ops failed");
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    /// <summary>Deletes the on-disk request files for every row under "Removed from spec".
    /// Each row is matched against the workspace tree by method + normalized path; misses
    /// are skipped silently.</summary>
    [RelayCommand]
    private void DeleteRemovedOps()
    {
        var root = _collections.ActiveCollection;
        if (root is null || string.IsNullOrEmpty(root.SourcePath))
        {
            StatusMessage = "No active collection.";
            return;
        }
        if (RemovedFromSpec.Count == 0) { StatusMessage = "Nothing to delete."; return; }

        var paths = new List<string>();
        foreach (var row in RemovedFromSpec)
        {
            var item = FindRequestItem(row.Method, row.Path);
            if (item?.SourcePath is not null) paths.Add(item.SourcePath);
        }
        if (paths.Count == 0)
        {
            StatusMessage = "Couldn't resolve removed requests to .bru files — re-Sync and try again.";
            return;
        }

        try
        {
            var deleted = OpenApiSpecApplier.DeleteRequestFiles(root.SourcePath, paths);
            _collections.LoadFromDirectory(root.SourcePath);
            StatusMessage = $"Deleted {deleted} request file(s). Re-syncing…";
            _ = SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delete removed ops failed");
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>Wipes the active collection's Bruno tree and rewrites it from the spec.
    /// Destructive — overwrites any local edits. Auxiliary directories (environments/,
    /// .git/, READMEs) are preserved.</summary>
    [RelayCommand]
    private void ReplaceCollection()
    {
        var root = _collections.ActiveCollection;
        if (root is null || string.IsNullOrEmpty(root.SourcePath))
        {
            StatusMessage = "No active collection.";
            return;
        }
        if (_lastSpec is null) { StatusMessage = "Run Sync first."; return; }

        try
        {
            OpenApiSpecApplier.ReplaceCollection(root.SourcePath, _lastSpec);
            _collections.LoadFromDirectory(root.SourcePath);
            StatusMessage = "Collection replaced from spec. Re-syncing…";
            _ = SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replace collection failed");
            StatusMessage = $"Replace failed: {ex.Message}";
        }
    }

    /// <summary>Quick-import path when no collection is loaded — writes the spec out as
    /// a fresh Bruno tree under the active workspace's <c>collections/</c> folder.
    /// Requires an active workspace; surfaces an actionable message otherwise.</summary>
    [RelayCommand]
    private void CreateCollectionFromSpec()
    {
        if (_lastSpec is null) { StatusMessage = "Run Sync first."; return; }
        var workspaceFolder = _workspaces?.ActiveWorkspace?.FolderPath;
        if (string.IsNullOrEmpty(workspaceFolder))
        {
            StatusMessage = "Open a workspace first (Workspaces panel → Open folder).";
            return;
        }

        try
        {
            var collectionsRoot = Path.Combine(workspaceFolder, "collections");
            Directory.CreateDirectory(collectionsRoot);
            var dest = ResolveImportFolder(collectionsRoot, _lastSpec.Name);
            BruCollectionWriter.Write(dest, _lastSpec);
            _collections.LoadFromDirectory(dest);
            StatusMessage = $"Created '{Path.GetFileName(dest)}' with {AddedInSpec.Count} operation(s).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Create collection from spec failed");
            StatusMessage = $"Create failed: {ex.Message}";
        }
    }

    private void ShowAsAdded(Collection liveSpec)
    {
        AddedInSpec.Clear(); RemovedFromSpec.Clear(); Unchanged.Clear();
        foreach (var r in FlattenRequests(liveSpec))
            AddedInSpec.Add(new DriftRow(r.Method, NormalizePath(r.Url), r.Name));
        LastSyncedSource = SpecSource;
        LastSyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        StatusMessage = $"Spec has {AddedInSpec.Count} operation(s). Click \"Create collection from this spec\" to import.";
    }

    private CollectionItemViewModel? FindRequestItem(string method, string normalizedPath)
    {
        foreach (var root in _collections.Roots)
        {
            var hit = FindIn(root, method, normalizedPath);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static CollectionItemViewModel? FindIn(CollectionNodeViewModel node, string method, string path)
    {
        if (node is CollectionItemViewModel item && item.Request is not null)
        {
            if (string.Equals(item.Request.Method, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePath(item.Request.Url), path, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        foreach (var child in node.Children)
        {
            var hit = FindIn(child, method, path);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Maps drift rows (method + normalized path) back to the corresponding
    /// RequestItem in the last-synced spec.</summary>
    private List<RequestItem> ResolveLiveRequests(IEnumerable<DriftRow> rows)
    {
        var result = new List<RequestItem>();
        if (_lastSpec is null) return result;
        var byKey = new Dictionary<string, RequestItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in FlattenRequests(_lastSpec))
            byKey[$"{r.Method}|{NormalizePath(r.Url)}"] = r;
        foreach (var row in rows)
            if (byKey.TryGetValue($"{row.Method}|{row.Path}", out var match))
                result.Add(match);
        return result;
    }

    private static string ResolveImportFolder(string collectionsRoot, string? specName)
    {
        var baseName = Sanitize(string.IsNullOrWhiteSpace(specName) ? "imported" : specName);
        if (string.IsNullOrEmpty(baseName)) baseName = "imported";
        var candidate = Path.Combine(collectionsRoot, baseName);
        if (!Directory.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var bumped = Path.Combine(collectionsRoot, $"{baseName} ({i})");
            if (!Directory.Exists(bumped)) return bumped;
        }
        return Path.Combine(collectionsRoot, baseName + "-" + DateTime.UtcNow.Ticks);
    }

    private static string Sanitize(string n)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(n.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static async Task<string> LoadSpecAsync(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            using var http = new HttpClient();
            return await http.GetStringAsync(uri).ConfigureAwait(false);
        }
        if (File.Exists(source))
        {
            return await File.ReadAllTextAsync(source).ConfigureAwait(false);
        }
        throw new FileNotFoundException($"Spec source not reachable: {source}");
    }

    private Collection? ActiveCollectionAsModel()
    {
        var root = _collections.Roots.OfType<CollectionRootViewModel>().FirstOrDefault();
        if (root is null) return null;

        var collection = new Collection { Name = root.Name };
        var rootRequests = new List<RequestItem>();
        var rootFolders = new List<Folder>();
        foreach (var child in root.Children)
        {
            switch (child)
            {
                case CollectionItemViewModel item when item.Request is not null:
                    rootRequests.Add(item.Request);
                    break;
                case CollectionFolderViewModel folder:
                    rootFolders.Add(BuildFolder(folder));
                    break;
            }
        }
        return collection with { Requests = rootRequests, Folders = rootFolders };
    }

    private static Folder BuildFolder(CollectionFolderViewModel folder)
    {
        var requests = new List<RequestItem>();
        var folders = new List<Folder>();
        foreach (var child in folder.Children)
        {
            switch (child)
            {
                case CollectionItemViewModel item when item.Request is not null:
                    requests.Add(item.Request);
                    break;
                case CollectionFolderViewModel sub:
                    folders.Add(BuildFolder(sub));
                    break;
            }
        }
        return new Folder { Name = folder.Name, Requests = requests, Folders = folders };
    }

    private static IEnumerable<RequestItem> FlattenRequests(Collection c)
    {
        foreach (var r in c.Requests) yield return r;
        foreach (var f in c.Folders)
            foreach (var r in WalkFolder(f)) yield return r;
    }

    private static IEnumerable<RequestItem> WalkFolder(Folder f)
    {
        foreach (var r in f.Requests) yield return r;
        foreach (var sub in f.Folders)
            foreach (var r in WalkFolder(sub)) yield return r;
    }

    private static string NormalizePath(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        const string baseUrlMarker = "{{baseUrl}}";
        var idx = url.IndexOf(baseUrlMarker, StringComparison.OrdinalIgnoreCase);
        var path = idx >= 0 ? url[(idx + baseUrlMarker.Length)..] : url;
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs)) path = abs.AbsolutePath;
        return path.TrimEnd('/');
    }
}

public sealed record DriftRow(string Method, string Path, string Name);
