using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Runner;
using Vegha.Core.Domain;
using Vegha.Core.Flow;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>Which sub-mode of the runner config is active — the Functional/Performance toggle
/// at the top of Postman's run configuration.</summary>
public enum RunModeKind { Functional, Performance }

/// <summary>Result filter tabs above the results list (All / Passed / Failed / Skipped /
/// Errors / Console log).</summary>
public enum ResultFilterKind { All, Passed, Failed, Skipped, Errors, ConsoleLog }

/// <summary>Which tab of the results detail split pane is showing.</summary>
public enum DetailTabKind { Response, Headers, Request }

/// <summary>Load shape for a performance test. Fixed holds VU count constant; Ramp climbs to it.</summary>
public enum PerfLoadProfile { Fixed, Ramp }

/// <summary>How the collection is launched (Postman's "Choose how to run"): interactively,
/// on a repeating in-app schedule, or by copying a CLI command for CI.</summary>
public enum RunHowKind { Manual, Schedule, Cli }

/// <summary>Layout of the results list — flat List rows or a denser Grid.</summary>
public enum ResultLayoutKind { List, Grid }

/// <summary>Tab that owns one Collection Runner execution: configuration (functional or
/// performance), live progress, and a Postman-style results view (summary bar, filter tabs,
/// per-request rows, and a click-to-open detail pane). Mirrors Postman's Collection Runner.</summary>
public sealed partial class CollectionRunTabViewModel : RequestTabViewModel
{
    public Collection TargetCollection { get; }

    private readonly HttpExecutor _http;
    private readonly JintHost _scripting;
    private readonly RequestComposition.WorkspaceContext? _workspace;
    private readonly Vegha.Integrations.Secrets.SecretRegistry? _secretRegistry;

    // -------- Mode + configuration --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFunctional), nameof(IsPerformance))]
    private RunModeKind _runMode = RunModeKind.Functional;

    public bool IsFunctional => RunMode == RunModeKind.Functional;
    public bool IsPerformance => RunMode == RunModeKind.Performance;

    /// <summary>Reorderable run sequence (Postman's "Run order"). Leaf request rows plus folder
    /// headers; the checked leaves, in this order, define what runs.</summary>
    public ObservableCollection<RunRequestRow> RequestRows { get; } = new();

    // Snapshot of the original tree order, used by Reset.
    private List<(string Name, string Method, bool IsGraphQL, IReadOnlyList<string> FolderNames)> _originalOrder = new();

    // How to run (Manual / Schedule / CLI).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual), nameof(IsSchedule), nameof(IsCli))]
    private RunHowKind _runHow = RunHowKind.Manual;

    public bool IsManual => RunHow == RunHowKind.Manual;
    public bool IsSchedule => RunHow == RunHowKind.Schedule;
    public bool IsCli => RunHow == RunHowKind.Cli;

    [ObservableProperty] private int _scheduleIntervalMinutes = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleButtonText))]
    private bool _isScheduleActive;
    public string ScheduleButtonText => IsScheduleActive ? "Stop schedule" : "Start schedule";

    /// <summary>The equivalent CLI invocation for CI automation. Regenerated when the
    /// collection path or environment changes.</summary>
    public string CliCommand => BuildCliCommand();

    [ObservableProperty] private int _iterations = 1;
    [ObservableProperty] private int _delayBetweenRequestsMs = 0;
    [ObservableProperty] private string? _dataFilePath;
    [ObservableProperty] private string? _dataFileStatus;

    // Advanced settings (Postman parity).
    [ObservableProperty] private bool _persistResponses = true;
    [ObservableProperty] private bool _turnOffLogs = false;
    [ObservableProperty] private bool _stopOnError = false;
    [ObservableProperty] private bool _keepVariableValues = true;
    [ObservableProperty] private bool _runWithoutStoredCookies = false;
    [ObservableProperty] private bool _saveCookiesAfterRun = true;
    [ObservableProperty] private bool _recordToHistory = false;

    // -------- Performance configuration --------

    // 0 = Fixed (all VUs from t0), 1 = Ramp up (VUs added gradually). Bound to the ComboBox.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRampProfile), nameof(LoadProfile))]
    private int _loadProfileIndex;
    public bool IsRampProfile => LoadProfileIndex == 1;
    public PerfLoadProfile LoadProfile => IsRampProfile ? PerfLoadProfile.Ramp : PerfLoadProfile.Fixed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    private int _virtualUsers = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    private int _testDurationMinutes = 10;

    /// <summary>LoadRunner/Postman-style starting concurrency: the run holds at this many VUs
    /// before ramping up to <see cref="VirtualUsers"/>. Clamped to [1, VirtualUsers].</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    private int _initialLoad = 5;

    /// <summary>Fraction of the total test where the ramp begins (end of the initial-hold phase).
    /// Two-way bound to the load-profile chart's start marker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    private double _rampStartFraction = 0.333;

    /// <summary>Fraction of the total test where the ramp ends (start of the target-hold phase).
    /// Two-way bound to the chart's end marker. Ramp span = (end − start) is always &lt; 1, so
    /// ramp-up time is always less than the total test duration.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    private double _rampEndFraction = 0.666;

    /// <summary>Live count of virtual users currently active — climbs during ramp-up, then
    /// plateaus. Surfaced in the performance summary bar.</summary>
    [ObservableProperty] private int _activeVirtualUsers;

    partial void OnVirtualUsersChanged(int value)
    {
        if (InitialLoad > value) InitialLoad = Math.Max(1, value);
    }

    /// <summary>Human-readable three-phase summary shown under the chart, e.g. "5 virtual users
    /// run for 3:20 minutes, ramp up to 20 for 3:20 minutes, then maintain 20 for 3:20 minutes…".</summary>
    public string PhaseDescription
    {
        get
        {
            var total = Math.Max(1, TestDurationMinutes) * 60.0;
            var s = Math.Clamp(RampStartFraction, 0, 1);
            var e = Math.Clamp(RampEndFraction, s, 1);
            var hold = s * total;
            var ramp = (e - s) * total;
            var sustain = (1 - e) * total;
            return $"{InitialLoad} virtual users run for {Mmss(hold)}, ramp up to {VirtualUsers} for " +
                   $"{Mmss(ramp)}, then maintain {VirtualUsers} for {Mmss(sustain)}, each executing all " +
                   $"selected requests sequentially.";
        }
    }

    private static string Mmss(double seconds)
    {
        var t = (int)Math.Round(Math.Max(0, seconds));
        return $"{t / 60}:{t % 60:00} minutes";
    }

    // -------- Live run state --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanCancel))]
    private bool _isRunning;

    [ObservableProperty] private bool _hasResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTestsCount))]
    private int _passedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _erroredCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private long _totalDurationMs;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private double _progressFraction;  // 0..1

    /// <summary>Postman-style "5s 865ms" duration for the summary bar.</summary>
    public string DurationDisplay => FormatDuration(TotalDurationMs);

    /// <summary>Count behind the "All" filter tab — every completed row.</summary>
    public int AllRowCount => _allRows.Count;

    // -------- Results view (summary bar + filter + rows + detail) --------

    /// <summary>Flat backing store of every completed request row, in arrival order. The
    /// filter tabs project from this into <see cref="VisibleResults"/>.</summary>
    private readonly List<RunResultRowVm> _allRows = new();

    /// <summary>What renders in the results list: iteration headers interleaved with request
    /// rows (or console lines when the Console log tab is active), filtered by
    /// <see cref="ActiveFilter"/>.</summary>
    public ObservableCollection<object> VisibleResults { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterPassed), nameof(IsFilterFailed),
        nameof(IsFilterSkipped), nameof(IsFilterErrors), nameof(IsFilterConsole))]
    private ResultFilterKind _activeFilter = ResultFilterKind.All;

    public bool IsFilterAll => ActiveFilter == ResultFilterKind.All;
    public bool IsFilterPassed => ActiveFilter == ResultFilterKind.Passed;
    public bool IsFilterFailed => ActiveFilter == ResultFilterKind.Failed;
    public bool IsFilterSkipped => ActiveFilter == ResultFilterKind.Skipped;
    public bool IsFilterErrors => ActiveFilter == ResultFilterKind.Errors;
    public bool IsFilterConsole => ActiveFilter == ResultFilterKind.ConsoleLog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetail))]
    private RunResultRowVm? _selectedRow;

    public bool ShowDetail => SelectedRow is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailResponse), nameof(IsDetailHeaders), nameof(IsDetailRequest))]
    private DetailTabKind _detailTab = DetailTabKind.Response;

    public bool IsDetailResponse => DetailTab == DetailTabKind.Response;
    public bool IsDetailHeaders => DetailTab == DetailTabKind.Headers;
    public bool IsDetailRequest => DetailTab == DetailTabKind.Request;

    [ObservableProperty] private ResultLayoutKind _resultLayout = ResultLayoutKind.List;
    public bool IsLayoutList => ResultLayout == ResultLayoutKind.List;

    // Summary bar (screenshot #1). Source is always "Runner" for an in-app run.
    public string SourceLabel => "Runner";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentDisplay), nameof(CliCommand))]
    private string? _environmentName;
    public string EnvironmentDisplay => string.IsNullOrEmpty(EnvironmentName) ? "No Environment" : EnvironmentName!;
    [ObservableProperty] private int _iterationsRun;
    [ObservableProperty] private int _consoleCount;
    [ObservableProperty] private long _avgResponseTimeMs;
    public int AllTestsCount => _allRows.Sum(r => r.Result.TotalTests);

    // Performance live stats.
    [ObservableProperty] private int _perfTotalRequests;
    [ObservableProperty] private long _perfAvgMs;
    [ObservableProperty] private double _perfRequestsPerSecond;
    [ObservableProperty] private double _perfErrorRate;
    public ObservableCollection<PerfSample> PerfSamples { get; } = new();

    public bool CanRun => !IsRunning;
    public bool CanCancel => IsRunning;
    public override object Workspace => this;

    private CancellationTokenSource? _cts;

    /// <summary>UI sync context captured at construction (the VM is created on the dispatcher).
    /// Performance runs execute on <c>Task.Run</c> threads and marshal chart/stat updates back
    /// through this. Null in headless tests — updates then run inline.</summary>
    private readonly System.Threading.SynchronizationContext? _uiContext = System.Threading.SynchronizationContext.Current;

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
        Kind = RequestKind.Http;
        _http = http;
        _scripting = scripting;
        _workspace = workspace;
        _secretRegistry = secretRegistry;

        BuildRequestRows();
    }

    // -------- Config: request rows --------

    private void BuildRequestRows()
    {
        // Flat list of leaf requests only — the run sequence is a flat ordered list, so folder
        // headers add no meaning here (they'd only be static, non-orderable labels). Requests
        // from nested folders are flattened in tree order.
        RequestRows.Clear();
        _originalOrder.Clear();
        foreach (var r in TargetCollection.Requests)
            AddRow(r.Name, r.Method, r.Kind == RequestKind.GraphQL, System.Array.Empty<string>());
        foreach (var folder in TargetCollection.Folders)
            AddFolderRequests(folder, new[] { folder.Name });
    }

    private void AddRow(string name, string method, bool isGraphQL, IReadOnlyList<string> folderNames)
    {
        RequestRows.Add(new RunRequestRow(name, method, indentLevel: 0, isFolderHeader: false, isGraphQL, folderNames));
        _originalOrder.Add((name, method, isGraphQL, folderNames));
    }

    private void AddFolderRequests(Folder folder, IReadOnlyList<string> folderNames)
    {
        foreach (var r in folder.Requests)
            AddRow(r.Name, r.Method, r.Kind == RequestKind.GraphQL, folderNames);
        foreach (var f in folder.Folders)
            AddFolderRequests(f, folderNames.Append(f.Name).ToArray());
    }

    [RelayCommand] private void SetRunMode(RunModeKind mode) => RunMode = mode;
    [RelayCommand] private void SetRunHow(RunHowKind how) => RunHow = how;

    /// <summary>Starts/stops a repeating in-app run on the configured interval. The loop runs on
    /// the captured UI context so each iteration's results render as a normal run; closing the
    /// tab or clicking again stops it.</summary>
    [RelayCommand]
    private void ToggleSchedule()
    {
        if (IsScheduleActive) { StopSchedule(); return; }
        _scheduleCts = new CancellationTokenSource();
        IsScheduleActive = true;
        var ct = _scheduleCts.Token;
        OnUi(() => _ = ScheduleLoopAsync(ct));
    }

    private void StopSchedule()
    {
        try { _scheduleCts?.Cancel(); } catch { }
        _scheduleCts?.Dispose();
        _scheduleCts = null;
        IsScheduleActive = false;
    }

    private CancellationTokenSource? _scheduleCts;

    private async Task ScheduleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsRunning) await RunAsync();
            try { await Task.Delay(Math.Max(1, ScheduleIntervalMinutes) * 60_000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Builds the CLI command that reproduces this run in CI: the collection path plus
    /// the selected environment. Iteration/order aren't CLI flags yet, so the command runs the
    /// whole collection once — noted in the UI.</summary>
    private string BuildCliCommand()
    {
        var path = string.IsNullOrEmpty(CollectionPath) ? "<collection-path>" : CollectionPath!;
        var quoted = path.Contains(' ') ? $"\"{path}\"" : path;
        var cmd = $"vegha run {quoted}";
        if (!string.IsNullOrEmpty(EnvironmentName))
            cmd += EnvironmentName!.Contains(' ') ? $" --env \"{EnvironmentName}\"" : $" --env {EnvironmentName}";
        return cmd;
    }
    [RelayCommand] private void SelectAll() { foreach (var r in RequestRows) if (!r.IsFolderHeader) r.IsSelected = true; }
    [RelayCommand] private void DeselectAll() { foreach (var r in RequestRows) if (!r.IsFolderHeader) r.IsSelected = false; }

    [RelayCommand]
    private void ResetOrder()
    {
        RequestRows.Clear();
        foreach (var (name, method, isGraphQL, folderNames) in _originalOrder)
            RequestRows.Add(new RunRequestRow(name, method, indentLevel: 0, isFolderHeader: false, isGraphQL, folderNames));
    }

    /// <summary>Moves a run-sequence row up one slot (folder headers included so groups move too).</summary>
    [RelayCommand]
    private void MoveRowUp(RunRequestRow? row)
    {
        if (row is null) return;
        var i = RequestRows.IndexOf(row);
        if (i > 0) RequestRows.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveRowDown(RunRequestRow? row)
    {
        if (row is null) return;
        var i = RequestRows.IndexOf(row);
        if (i >= 0 && i < RequestRows.Count - 1) RequestRows.Move(i, i + 1);
    }

    // -------- Config: filter tab + detail selection --------

    [RelayCommand] private void SetFilter(ResultFilterKind filter) { ActiveFilter = filter; RebuildVisible(); }
    [RelayCommand] private void SetDetailTab(DetailTabKind tab) => DetailTab = tab;
    [RelayCommand] private void OpenRow(RunResultRowVm? row) { if (row is not null) { SelectedRow = row; DetailTab = DetailTabKind.Response; } }
    [RelayCommand] private void CloseDetail() => SelectedRow = null;
    [RelayCommand] private void SetLayout(ResultLayoutKind layout) => ResultLayout = layout;

    // -------- Data file --------

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
    public void ClearDataFile() { DataFilePath = null; DataFileStatus = null; }

    // -------- Run (functional) --------

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        if (IsPerformance) { await RunPerformanceAsync(); return; }
        if (IsRunning) return;
        BeginRun();

        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        try
        {
            var ds = await LoadDataSourceAsync(_cts.Token);
            if (ds is null && DataFileStatus?.StartsWith("Data file:") == true) { IsRunning = false; return; }

            var orderedNames = RequestRows
                .Where(r => !r.IsFolderHeader && r.IsSelected)
                .Select(r => r.Key)
                .ToList();

            var options = new RunnerOptions(
                Collection: TargetCollection,
                SelectedRequestNames: null,
                Iterations: Iterations,
                Workers: 1,
                DelayBetweenRequestsMs: DelayBetweenRequestsMs,
                DataSource: ds,
                EnvironmentVariables: EnvironmentVariables ?? new Dictionary<string, string>(),
                RecordToHistory: RecordToHistory,
                IsolatedCookieJarPerRun: RunWithoutStoredCookies,
                PersistResponses: PersistResponses,
                StopOnError: StopOnError,
                KeepVariableValues: KeepVariableValues,
                TurnOffLogs: TurnOffLogs,
                OrderedRequestNames: orderedNames);

            IterationsRun = options.EffectiveIterations;

            var executor = new PipelineRequestExecutor(
                _http, _scripting, _workspace, _secretRegistry,
                persistResponses: PersistResponses, turnOffLogs: TurnOffLogs, keepVariableValues: KeepVariableValues);

            var totalRequestCount = Math.Max(1, orderedNames.Count) * options.EffectiveIterations;
            var completed = 0;

            var progress = new Progress<RunnerEvent>(evt =>
            {
                switch (evt)
                {
                    case RunStarted rs:
                        StatusMessage = $"Running {orderedNames.Count} request(s) across {rs.TotalIterations} iteration(s)…";
                        break;
                    case RequestCompleted rc:
                        AddResultRow(rc.IterationIndex, rc.Result);
                        completed++;
                        if (totalRequestCount > 0) ProgressFraction = (double)completed / totalRequestCount;
                        break;
                    case RunCompleted rcomp:
                        TotalDurationMs = rcomp.DurationMs;
                        StatusMessage = rcomp.WasCanceled
                            ? $"Canceled — {rcomp.Passed} passed, {rcomp.Failed} failed in {FormatDuration(rcomp.DurationMs)}"
                            : $"Done — {rcomp.Passed} passed, {rcomp.Failed} failed in {FormatDuration(rcomp.DurationMs)}";
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
            TotalDurationMs = sw.ElapsedMilliseconds;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // -------- Run (performance) --------

    /// <summary>Drives N virtual users concurrently, each looping the run sequence for the
    /// configured duration. Collects throughput + response-time samples for the live chart and
    /// rolls up aggregate stats. Uses the same pipeline executor as a functional run.</summary>
    private async Task RunPerformanceAsync()
    {
        if (IsRunning) return;
        BeginRun();
        PerfSamples.Clear();
        PerfTotalRequests = 0; PerfAvgMs = 0; PerfRequestsPerSecond = 0; PerfErrorRate = 0; ActiveVirtualUsers = 0;

        var orderedNames = RequestRows
            .Where(r => !r.IsFolderHeader && r.IsSelected)
            .Select(r => r.Key)
            .ToList();
        if (orderedNames.Count == 0) { StatusMessage = "No requests selected."; IsRunning = false; return; }

        // Map each run-order key back to its (request, folder-chain) once for the whole test.
        var keyMap = BuildKeyMap();

        _cts = new CancellationTokenSource();
        var durationMs = Math.Max(1, TestDurationMinutes) * 60_000L;
        var deadline = Stopwatch.StartNew();
        var vus = Math.Max(1, VirtualUsers);

        // Three-phase load profile. Fixed → all VUs from t0. Ramp → hold at InitialLoad, ramp to
        // target between the two markers, then hold at target. The ramp window is a strict
        // sub-segment of the test, so ramp-up time is always less than the total duration.
        var ramp = IsRampProfile;
        var initialLoad = ramp ? Math.Clamp(InitialLoad, 1, vus) : vus;
        var rampStartMs = ramp ? (long)(Math.Clamp(RampStartFraction, 0, 1) * durationMs) : 0L;
        var rampEndMs = ramp ? (long)(Math.Clamp(RampEndFraction, 0, 1) * durationMs) : 0L;
        if (rampEndMs < rampStartMs) rampEndMs = rampStartMs;

        // Thread-safe running tallies.
        long totalRequests = 0, totalErrors = 0, totalElapsed = 0, activeVus = 0;
        var windowLock = new object();
        long windowCount = 0;

        var executor = new PipelineRequestExecutor(
            _http, _scripting, _workspace, _secretRegistry,
            persistResponses: false, turnOffLogs: true, keepVariableValues: KeepVariableValues);
        var runDelegate = executor.AsDelegate(TargetCollection, EnvironmentVariables ?? new Dictionary<string, string>());

        var options = new RunnerOptions(
            Collection: TargetCollection, SelectedRequestNames: null,
            Iterations: 1, Workers: 1, DelayBetweenRequestsMs: 0, DataSource: null,
            EnvironmentVariables: EnvironmentVariables ?? new Dictionary<string, string>(),
            OrderedRequestNames: orderedNames);

        StatusMessage = ramp
            ? $"Performance: {initialLoad} → {vus} virtual users over a {TestDurationMinutes} min test…"
            : $"Performance: {vus} virtual users for {TestDurationMinutes} min…";

        // Sampling loop on the UI context — updates the chart ~1/sec.
        var sampler = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested && deadline.ElapsedMilliseconds < durationMs)
            {
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
                long count, elapsed, errs;
                lock (windowLock) { count = windowCount; windowCount = 0; }
                elapsed = Interlocked.Read(ref totalElapsed);
                var reqs = Interlocked.Read(ref totalRequests);
                errs = Interlocked.Read(ref totalErrors);
                var avg = reqs > 0 ? elapsed / reqs : 0;
                var active = (int)Interlocked.Read(ref activeVus);
                OnUi(() =>
                {
                    PerfTotalRequests = (int)reqs;
                    PerfAvgMs = avg;
                    PerfRequestsPerSecond = count;
                    PerfErrorRate = reqs > 0 ? (double)errs / reqs : 0;
                    ActiveVirtualUsers = active;
                    ProgressFraction = Math.Min(1.0, (double)deadline.ElapsedMilliseconds / durationMs);
                    AddPerfSample(count);
                });
            }
        });

        try
        {
            var workers = Enumerable.Range(0, vus).Select(vu => Task.Run(async () =>
            {
                // Staggered start for the ramp: the first `initialLoad` VUs go live at t0, the rest
                // come online linearly between the two markers, reaching the target at the ramp end.
                if (ramp)
                {
                    var startAt = VuStartOffsetMs(vu, initialLoad, vus, rampStartMs, rampEndMs);
                    var wait = startAt - deadline.ElapsedMilliseconds;
                    if (wait > 0)
                    {
                        try { await Task.Delay((int)Math.Min(wait, int.MaxValue), _cts.Token); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                if (_cts.Token.IsCancellationRequested || deadline.ElapsedMilliseconds >= durationMs) return;

                Interlocked.Increment(ref activeVus);
                try
                {
                    while (!_cts.Token.IsCancellationRequested && deadline.ElapsedMilliseconds < durationMs)
                    {
                        foreach (var key in orderedNames)
                        {
                            if (_cts.Token.IsCancellationRequested || deadline.ElapsedMilliseconds >= durationMs) break;
                            if (!keyMap.TryGetValue(key, out var plan)) continue;
                            var rowSw = Stopwatch.StartNew();
                            RequestRunResult res;
                            try { res = await runDelegate(vu, plan.Request, plan.Chain, EmptyVars, _cts.Token); }
                            catch { res = null!; }
                            rowSw.Stop();
                            Interlocked.Increment(ref totalRequests);
                            Interlocked.Add(ref totalElapsed, res?.ElapsedMs ?? rowSw.ElapsedMilliseconds);
                            if (res is null || res.Status is RequestRunStatus.Failed or RequestRunStatus.Errored)
                                Interlocked.Increment(ref totalErrors);
                            lock (windowLock) windowCount++;
                        }
                    }
                }
                finally { Interlocked.Decrement(ref activeVus); }
            }, _cts.Token)).ToArray();

            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) { /* stopped */ }
        finally
        {
            try { _cts.Cancel(); } catch { }
            await sampler.ContinueWith(_ => { });
            var reqs = Interlocked.Read(ref totalRequests);
            var errs = Interlocked.Read(ref totalErrors);
            var elapsed = Interlocked.Read(ref totalElapsed);
            PerfTotalRequests = (int)reqs;
            PerfAvgMs = reqs > 0 ? elapsed / reqs : 0;
            PerfErrorRate = reqs > 0 ? (double)errs / reqs : 0;
            ActiveVirtualUsers = 0;
            TotalDurationMs = deadline.ElapsedMilliseconds;
            StatusMessage = $"Performance done — {reqs} requests, {PerfAvgMs} ms avg, {PerfErrorRate:P1} errors";
            ProgressFraction = 1.0;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Time (ms from test start) at which virtual user <paramref name="vu"/> (0-based)
    /// comes online in the three-phase ramp: the first <paramref name="initialLoad"/> VUs start at
    /// t0 (the initial-hold phase), and the remaining VUs are spread linearly across the ramp
    /// window [<paramref name="rampStartMs"/>, <paramref name="rampEndMs"/>], with the last one
    /// reaching the target exactly at the ramp end. Exposed for testing the schedule.</summary>
    public static long VuStartOffsetMs(int vu, int initialLoad, int totalVus, long rampStartMs, long rampEndMs)
    {
        if (vu < initialLoad) return 0;
        var rampCount = totalVus - initialLoad;
        if (rampCount <= 0) return 0;
        var k = vu - initialLoad;                 // 0-based index among the ramping VUs
        var span = Math.Max(0, rampEndMs - rampStartMs);
        return rampStartMs + span * (k + 1) / rampCount;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyVars = new Dictionary<string, string>();

    /// <summary>Maps each request's run-order key (folder path + name) to its (request, chain).
    /// First match wins on a true duplicate key (same name in the same folder).</summary>
    private Dictionary<string, (RequestItem Request, IReadOnlyList<Folder> Chain)> BuildKeyMap()
    {
        var map = new Dictionary<string, (RequestItem, IReadOnlyList<Folder>)>(StringComparer.Ordinal);

        void Walk(IList<RequestItem> reqs, IList<Folder> folders, List<Folder> chain)
        {
            foreach (var r in reqs)
            {
                var key = CollectionRunOrchestrator.RequestKey(chain, r);
                if (!map.ContainsKey(key)) map[key] = (r, chain.ToArray());
            }
            foreach (var f in folders)
            {
                chain.Add(f);
                Walk(f.Requests, f.Folders, chain);
                chain.RemoveAt(chain.Count - 1);
            }
        }

        Walk(TargetCollection.Requests, TargetCollection.Folders, new List<Folder>());
        return map;
    }

    private void AddPerfSample(double rps)
    {
        var max = Math.Max(1.0, PerfSamples.Count == 0 ? rps : Math.Max(rps, PerfSamples.Max(s => s.Value)));
        PerfSamples.Add(new PerfSample(rps, Math.Min(100.0, rps / max * 100.0)));
        while (PerfSamples.Count > 120) PerfSamples.RemoveAt(0);
    }

    // -------- Cancel / reset --------

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
        StopSchedule();
        HasResults = false;
        _allRows.Clear();
        VisibleResults.Clear();
        SelectedRow = null;
        PassedCount = FailedCount = ErroredCount = SkippedCount = ConsoleCount = 0;
        TotalDurationMs = AvgResponseTimeMs = 0;
        ProgressFraction = 0;
        StatusMessage = null;
        OnPropertyChanged(nameof(AllTestsCount));
        OnPropertyChanged(nameof(AllRowCount));
    }

    // -------- Results plumbing --------

    private void BeginRun()
    {
        IsRunning = true;
        HasResults = true;
        _allRows.Clear();
        VisibleResults.Clear();
        SelectedRow = null;
        ActiveFilter = ResultFilterKind.All;
        PassedCount = FailedCount = ErroredCount = SkippedCount = ConsoleCount = 0;
        TotalDurationMs = AvgResponseTimeMs = 0;
        ProgressFraction = 0;
        StatusMessage = "Starting…";
        OnPropertyChanged(nameof(AllTestsCount));
        OnPropertyChanged(nameof(AllRowCount));
    }

    private async Task<IterationDataSource?> LoadDataSourceAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DataFilePath)) return null;
        try { return await IterationDataSource.LoadAsync(DataFilePath, ct); }
        catch (Exception ex) { DataFileStatus = "Data file: " + ex.Message; return null; }
    }

    private void AddResultRow(int iterationIndex, RequestRunResult result)
    {
        var row = new RunResultRowVm(result, iterationIndex);
        _allRows.Add(row);

        switch (result.Status)
        {
            case RequestRunStatus.Passed: PassedCount++; break;
            case RequestRunStatus.Failed: FailedCount++; break;
            case RequestRunStatus.Errored: ErroredCount++; break;
            case RequestRunStatus.Skipped: SkippedCount++; break;
        }
        if (result.Detail is { Console.Count: > 0 }) ConsoleCount += result.Detail.Console.Count;

        // Running average response time over rows that actually made a call.
        var timed = _allRows.Where(r => r.Result.ElapsedMs > 0).ToList();
        AvgResponseTimeMs = timed.Count > 0 ? (long)timed.Average(r => r.Result.ElapsedMs) : 0;
        OnPropertyChanged(nameof(AllTestsCount));
        OnPropertyChanged(nameof(AllRowCount));

        if (RowMatchesFilter(row) && ActiveFilter != ResultFilterKind.ConsoleLog)
            AppendRowWithHeader(row);
        else if (ActiveFilter == ResultFilterKind.ConsoleLog)
            AppendConsoleLines(row);
    }

    private readonly HashSet<int> _headeredIterations = new();

    private void AppendRowWithHeader(RunResultRowVm row)
    {
        if (_headeredIterations.Add(row.IterationIndex))
            VisibleResults.Add(new RunIterationHeader($"Iteration {row.IterationIndex + 1}"));
        VisibleResults.Add(row);
    }

    private void AppendConsoleLines(RunResultRowVm row)
    {
        if (row.Result.Detail is not { } d) return;
        foreach (var line in d.Console)
            VisibleResults.Add(new RunConsoleLineVm(line.Level, line.Text, row.Name));
    }

    private void RebuildVisible()
    {
        VisibleResults.Clear();
        _headeredIterations.Clear();

        if (ActiveFilter == ResultFilterKind.ConsoleLog)
        {
            foreach (var row in _allRows) AppendConsoleLines(row);
            return;
        }
        foreach (var row in _allRows)
            if (RowMatchesFilter(row)) AppendRowWithHeader(row);
    }

    private bool RowMatchesFilter(RunResultRowVm row) => ActiveFilter switch
    {
        ResultFilterKind.All => true,
        ResultFilterKind.Passed => row.Result.Status == RequestRunStatus.Passed,
        ResultFilterKind.Failed => row.Result.Status == RequestRunStatus.Failed,
        ResultFilterKind.Skipped => row.Result.Status == RequestRunStatus.Skipped,
        ResultFilterKind.Errors => row.Result.Status == RequestRunStatus.Errored,
        _ => true,
    };

    /// <summary>External hook for the host to thread the active environment variables in.
    /// Re-set whenever the env switches; reads happen at RunAsync time so a mid-run env
    /// change does not affect an in-flight run.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    // -------- formatting helpers --------

    /// <summary>"5s 865ms" / "865 ms" — Postman's duration format.</summary>
    public static string FormatDuration(long ms)
    {
        if (ms < 1000) return $"{ms} ms";
        var s = ms / 1000; var rem = ms % 1000;
        if (s < 60) return $"{s}s {rem}ms";
        var m = s / 60; s %= 60;
        return $"{m}m {s}s";
    }

    /// <summary>"918 B" / "1.509 KB" / "2.4 MB" — Postman's size format.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.###} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private void OnUi(Action action)
    {
        // Performance runs execute on Task.Run threads; marshal chart/stat updates onto the
        // captured UI context. Headless (no context) runs inline.
        if (_uiContext is not null) _uiContext.Post(_ => action(), null);
        else action();
    }
}

/// <summary>One selectable request row in the runner's reorderable run sequence.</summary>
public partial class RunRequestRow : ObservableObject
{
    public string Name { get; }
    public string Method { get; }
    public int IndentLevel { get; }
    public bool IsFolderHeader { get; }
    /// <summary>True for GraphQL requests — the row shows the GraphQL mark instead of the verb.</summary>
    public bool IsGraphQL { get; }

    /// <summary>Folder names from the collection root down to this request. Empty at root.</summary>
    public IReadOnlyList<string> FolderNames { get; }

    /// <summary>Displayed folder breadcrumb (e.g. "Auth / Login") — disambiguates same-named
    /// requests that live in different folders. Empty for root-level requests.</summary>
    public string FolderPath => FolderNames.Count == 0 ? string.Empty : string.Join(" / ", FolderNames);
    public bool HasFolderPath => FolderNames.Count > 0;

    /// <summary>Stable identity used for run ordering — folder path + name, so duplicate request
    /// names in different folders resolve to distinct requests.</summary>
    public string Key => CollectionRunOrchestrator.RequestKey(FolderNames, Name);

    [ObservableProperty] private bool _isSelected = true;

    public RunRequestRow(string name, string method, int indentLevel, bool isFolderHeader = false,
        bool isGraphQL = false, IReadOnlyList<string>? folderNames = null)
    {
        Name = name; Method = method;
        IndentLevel = indentLevel; IsFolderHeader = isFolderHeader;
        IsGraphQL = isGraphQL;
        FolderNames = folderNames ?? System.Array.Empty<string>();
    }
}

/// <summary>Section header between iterations in the results list.</summary>
public sealed class RunIterationHeader
{
    public string Label { get; }
    public RunIterationHeader(string label) => Label = label;
}

/// <summary>One console line surfaced under the Console log filter tab.</summary>
public sealed class RunConsoleLineVm
{
    public string Level { get; }
    public string Text { get; }
    public string Source { get; }
    public RunConsoleLineVm(string level, string text, string source) { Level = level; Text = text; Source = source; }
}

/// <summary>One request result row in the results list, with display-ready projections and the
/// captured detail for the Response/Headers/Request split pane.</summary>
public sealed class RunResultRowVm
{
    public RequestRunResult Result { get; }
    public int IterationIndex { get; }

    public RunResultRowVm(RequestRunResult result, int iterationIndex)
    {
        Result = result;
        IterationIndex = iterationIndex;
    }

    public string Method => Result.Method;
    public bool IsGraphQL => Result.IsGraphQL;
    public string Name => Result.Name;
    public string Url => Result.Url;
    public int StatusCode => Result.StatusCode;
    public string StatusText => StatusCode > 0 ? StatusCode.ToString() : Result.Status.ToString();
    public long ElapsedMs => Result.ElapsedMs;
    public string SizeText => CollectionRunTabViewModel.FormatSize(Result.ResponseSizeBytes);
    public bool HasTests => Result.TotalTests > 0;
    public string TestsSummary => HasTests ? $"{Result.PassedTests} passed, {Result.FailedTests} failed" : "No tests found";

    /// <summary>Folder breadcrumb (e.g. "Auth / Login") disambiguating same-named requests.</summary>
    public string FolderPath => Result.FolderPath;
    public bool HasFolderPath => !string.IsNullOrEmpty(Result.FolderPath);

    public string Breadcrumb => HasFolderPath ? $"{Result.FolderPath} / {Result.Name}" : Result.Name;
    public string StatusTimeSize => $"{StatusText}  •  {ElapsedMs} ms  •  {SizeText}";

    // Detail-pane projections. Empty strings render cleanly when no detail was captured.
    public string ResponseBodyText => Pretty(Result.Detail?.ResponseBody ?? string.Empty);
    public string ResponseHeadersText => FormatHeaders(Result.Detail?.ResponseHeaders);
    public string RequestHeadersText => FormatHeaders(Result.Detail?.RequestHeaders);
    public string RequestBodyText => Result.Detail?.RequestBody ?? string.Empty;
    public string RequestLine => $"{Result.Method} {Result.Url}";

    private static string FormatHeaders(IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        if (headers is null || headers.Count == 0) return string.Empty;
        return string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"));
    }

    /// <summary>Pretty-print JSON bodies; leave everything else (XML, text) untouched.</summary>
    private static string Pretty(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return body;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch { return body; }
    }
}

/// <summary>One point on the performance throughput sparkline (raw value + normalized height %).</summary>
public sealed class PerfSample
{
    public double Value { get; }
    public double HeightPercent { get; }
    public PerfSample(double value, double heightPercent) { Value = value; HeightPercent = heightPercent; }
}
