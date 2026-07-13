using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Runner;
using Vegha.Core.Domain;
using Vegha.Core.Flow;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>Tab that owns one Collection Runner execution: configuration, live progress, and
/// per-request results. Mirrors how Bruno's <c>collection-runner</c> tab type works — config
/// panel on top, results render below as requests complete.</summary>
public sealed partial class CollectionRunTabViewModel : RequestTabViewModel
{
    public Collection TargetCollection { get; }

    private readonly HttpExecutor _http;
    private readonly JintHost _scripting;
    private readonly RequestComposition.WorkspaceContext? _workspace;
    private readonly Vegha.Integrations.Secrets.SecretRegistry? _secretRegistry;

    // -------- Configuration (pre-run + during-config) --------

    /// <summary>Tree of request rows the user can check/uncheck. Hierarchy preserved via the
    /// IndentLevel field for indentation rendering; we don't use TreeView nesting because the
    /// runner UI is fundamentally a flat list of results once running.</summary>
    public ObservableCollection<RunRequestRow> RequestRows { get; } = new();

    [ObservableProperty] private int _iterations = 1;
    [ObservableProperty] private int _workers = 1;
    [ObservableProperty] private int _delayBetweenRequestsMs = 0;
    [ObservableProperty] private string? _dataFilePath;
    [ObservableProperty] private string? _dataFileStatus;
    [ObservableProperty] private bool _recordToHistory = false;
    [ObservableProperty] private bool _isolatedCookieJarPerRun = true;

    // -------- Live run state --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanCancel))]
    private bool _isRunning;

    [ObservableProperty] private bool _hasResults;

    /// <summary>Per-iteration result groups. Each iteration adds a group; rows append within
    /// as RequestCompleted events arrive. Iteration index doubles as the group's display order.</summary>
    public ObservableCollection<IterationResultGroup> Iterations_Results { get; } = new();

    [ObservableProperty] private int _passedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _erroredCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private long _totalDurationMs;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private double _progressFraction;  // 0..1

    public bool CanRun => !IsRunning;
    public bool CanCancel => IsRunning;

    public override object Workspace => this;

    private CancellationTokenSource? _cts;

    public CollectionRunTabViewModel(
        Collection collection,
        string id,
        HttpExecutor http,
        JintHost scripting,
        RequestComposition.WorkspaceContext? workspace = null,
        Vegha.Integrations.Secrets.SecretRegistry? secretRegistry = null)
    {
        TargetCollection = collection;
        Id = id;
        Name = collection.Name + " (Runner)";
        Method = "RUN";
        Kind = RequestKind.Http;  // closest match — the tab strip doesn't switch on it for run tabs
        _http = http;
        _scripting = scripting;
        _workspace = workspace;
        _secretRegistry = secretRegistry;

        BuildRequestRows();
    }

    private void BuildRequestRows()
    {
        RequestRows.Clear();
        foreach (var r in TargetCollection.Requests)
            RequestRows.Add(new RunRequestRow(r.Name, r.Method, indentLevel: 0, isGraphQL: r.Kind == RequestKind.GraphQL));
        foreach (var folder in TargetCollection.Folders)
            AddFolder(folder, depth: 1);
    }

    private void AddFolder(Folder folder, int depth)
    {
        RequestRows.Add(new RunRequestRow(folder.Name, "FOLDER", depth, isFolderHeader: true));
        foreach (var r in folder.Requests)
            RequestRows.Add(new RunRequestRow(r.Name, r.Method, depth + 1, isGraphQL: r.Kind == RequestKind.GraphQL));
        foreach (var f in folder.Folders) AddFolder(f, depth + 1);
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var anyUnchecked = RequestRows.Any(r => !r.IsFolderHeader && !r.IsSelected);
        foreach (var r in RequestRows)
            if (!r.IsFolderHeader) r.IsSelected = anyUnchecked;
    }

    [RelayCommand]
    public async Task PickDataFileAsync(string path)
    {
        DataFilePath = path;
        try
        {
            var ds = await IterationDataSource.LoadAsync(path);
            DataFileStatus = $"{ds.RowCount} row(s) • {ds.Columns.Count} column(s)";
        }
        catch (Exception ex)
        {
            DataFilePath = null;
            DataFileStatus = "Failed: " + ex.Message;
        }
    }

    [RelayCommand]
    public void ClearDataFile()
    {
        DataFilePath = null;
        DataFileStatus = null;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        HasResults = true;
        Iterations_Results.Clear();
        PassedCount = FailedCount = ErroredCount = SkippedCount = 0;
        TotalDurationMs = 0;
        ProgressFraction = 0;
        StatusMessage = "Starting…";

        _cts = new CancellationTokenSource();
        try
        {
            IterationDataSource? ds = null;
            if (!string.IsNullOrEmpty(DataFilePath))
            {
                try { ds = await IterationDataSource.LoadAsync(DataFilePath, _cts.Token); }
                catch (Exception ex) { StatusMessage = "Data file: " + ex.Message; IsRunning = false; return; }
            }

            var selectedNames = RequestRows
                .Where(r => !r.IsFolderHeader && r.IsSelected)
                .Select(r => r.Name)
                .ToHashSet(StringComparer.Ordinal);

            var options = new RunnerOptions(
                Collection: TargetCollection,
                SelectedRequestNames: selectedNames.Count == CountLeafRows() ? null : selectedNames,
                Iterations: Iterations,
                Workers: Workers,
                DelayBetweenRequestsMs: DelayBetweenRequestsMs,
                DataSource: ds,
                EnvironmentVariables: EnvironmentVariables ?? new Dictionary<string, string>(),
                RecordToHistory: RecordToHistory,
                IsolatedCookieJarPerRun: IsolatedCookieJarPerRun);

            var executor = new PipelineRequestExecutor(_http, _scripting, _workspace, _secretRegistry);
            var totalRequestCount = CollectionRunOrchestrator.CountRequests(TargetCollection) * options.EffectiveIterations;
            var completed = 0;

            var progress = new Progress<RunnerEvent>(evt =>
            {
                // We're on the captured sync context — UI thread for an Avalonia VM created
                // on the dispatcher. Safe to touch observable collections directly.
                switch (evt)
                {
                    case RunStarted rs:
                        StatusMessage = $"Running {rs.TotalRequests} request(s) across {rs.TotalIterations} iteration(s)…";
                        break;
                    case IterationStarted ist:
                        Iterations_Results.Add(new IterationResultGroup(ist.Index));
                        break;
                    case RequestCompleted rc:
                        var group = Iterations_Results.FirstOrDefault(g => g.Index == rc.IterationIndex)
                                    ?? Iterations_Results.LastOrDefault();
                        group?.Results.Add(rc.Result);
                        switch (rc.Result.Status)
                        {
                            case RequestRunStatus.Passed:   PassedCount++; break;
                            case RequestRunStatus.Failed:   FailedCount++; break;
                            case RequestRunStatus.Errored:  ErroredCount++; break;
                            case RequestRunStatus.Skipped:  SkippedCount++; break;
                            case RequestRunStatus.Canceled: break;
                        }
                        completed++;
                        if (totalRequestCount > 0) ProgressFraction = (double)completed / totalRequestCount;
                        break;
                    case RunCompleted rcomp:
                        TotalDurationMs = rcomp.DurationMs;
                        StatusMessage = rcomp.WasCanceled
                            ? $"Canceled — {rcomp.Passed} passed, {rcomp.Failed} failed in {rcomp.DurationMs} ms"
                            : $"Done — {rcomp.Passed} passed, {rcomp.Failed} failed in {rcomp.DurationMs} ms";
                        ProgressFraction = 1.0;
                        break;
                }
            });

            await CollectionRunOrchestrator.RunAsync(
                options,
                executor.AsDelegate(TargetCollection, options.EnvironmentVariables),
                progress,
                _cts.Token);
        }
        catch (Exception ex)
        {
            StatusMessage = "Run failed: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* best-effort */ }
        StatusMessage = "Canceling…";
    }

    [RelayCommand]
    public void ResetResults()
    {
        if (IsRunning) return;
        HasResults = false;
        Iterations_Results.Clear();
        PassedCount = FailedCount = ErroredCount = SkippedCount = 0;
        TotalDurationMs = 0;
        ProgressFraction = 0;
        StatusMessage = null;
    }

    /// <summary>External hook for the host to thread the active environment variables in.
    /// Re-set whenever the env switches; reads happen at RunAsync time so a mid-run env
    /// change does not affect an in-flight run (intentional — matches Bruno's snapshot model).</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    private int CountLeafRows() => RequestRows.Count(r => !r.IsFolderHeader);
}

/// <summary>One selectable / folder-header row in the runner config tree.</summary>
public partial class RunRequestRow : ObservableObject
{
    public string Name { get; }
    public string Method { get; }
    public int IndentLevel { get; }
    public bool IsFolderHeader { get; }
    /// <summary>True for GraphQL requests — the row shows the GraphQL mark instead of the verb.</summary>
    public bool IsGraphQL { get; }

    [ObservableProperty] private bool _isSelected = true;

    public RunRequestRow(string name, string method, int indentLevel, bool isFolderHeader = false, bool isGraphQL = false)
    {
        Name = name; Method = method;
        IndentLevel = indentLevel; IsFolderHeader = isFolderHeader;
        IsGraphQL = isGraphQL;
    }
}

/// <summary>Per-iteration grouping of request results — rendered as a collapsible section.</summary>
public partial class IterationResultGroup : ObservableObject
{
    public int Index { get; }
    public string Label => $"Iteration {Index + 1}";
    public ObservableCollection<RequestRunResult> Results { get; } = new();

    public IterationResultGroup(int index) { Index = index; }
}
