using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.App.ViewModels.Scriptless;
using Vegha.App.ViewModels.Services;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;
using Vegha.Core.Requests;
using Vegha.Core.Scripting.Testing;

namespace Vegha.App.ViewModels;

/// <summary>
/// Scriptless testing + request chaining for the request editor: Capture rows (set variables
/// from the response), Assertion rows, and linked requests (Chain). Rows round-trip through
/// <c>vars:post-response[:env|:global]</c>, <c>assert</c> and <c>chain:pre-request</c>; this file
/// owns their state, live previews against the last response, and the Send-time steps.
/// </summary>
public partial class RequestEditorViewModel
{
    private RuntimeVariableStore _runtimeStore = new();
    private LastResponseCache _responseCache = new();

    /// <summary>"Set variable from response" rows (all scopes, in display order).</summary>
    public ObservableCollection<CaptureRow> Captures { get; } = new();

    /// <summary>Scriptless assertion rows.</summary>
    public ObservableCollection<AssertionRow> Assertions { get; } = new();

    /// <summary>Linked requests run before this one.</summary>
    public ObservableCollection<ChainLinkRow> ChainLinks { get; } = new();

    /// <summary>Outcome of the linked requests on the last Send (the response pane's chain strip).</summary>
    public ObservableCollection<ChainStep> ChainSteps { get; } = new();

    public bool HasChainSteps => ChainSteps.Count > 0;

    /// <summary>Whether the Tests tab's JavaScript editor is open. Collapsed by default so the
    /// no-code tables get the room; opened on load when the request already has a script.</summary>
    [ObservableProperty] private bool _isTestsScriptExpanded;

    /// <summary>Session runtime variables shared across tabs (bindable for the Runtime panel).</summary>
    public RuntimeVariableStore RuntimeVariables => _runtimeStore;

    public IReadOnlyList<SourceOption> CaptureSourceOptions => ScriptlessOptions.Sources;
    public IReadOnlyList<SourceOption> AssertionSourceOptions => ScriptlessOptions.Sources;
    public IReadOnlyList<ScopeOption> CaptureScopeOptions => ScriptlessOptions.Scopes;
    public IReadOnlyList<AssertionOperator> AssertionOperatorOptions => ScriptlessOptions.Operators;
    public IReadOnlyList<ChainModeOption> ChainModeOptions => ScriptlessOptions.ChainModes;

    public int CapturesCount => Captures.Count(c => !c.IsBlank);
    public int AssertionsCount => Assertions.Count(a => !a.IsBlank);
    public int ChainLinksCount => ChainLinks.Count(l => !l.IsBlank);
    /// <summary>Badge for the Tests tab: captures + assertions.</summary>
    public int ScriptlessCount => CapturesCount + AssertionsCount;

    /// <summary>Paths of every other request in the parent collection — the Chain row's picker.</summary>
    public IReadOnlyList<string> LinkableRequestPaths
    {
        get
        {
            if (_parentCollection is null) return Array.Empty<string>();
            var ctx = new ChainContext(_parentCollection, null, _responseCache);
            var self = SourcePath;
            return ctx.Enumerate()
                .Where(x => !string.Equals(x.Request.SourcePath, self, StringComparison.OrdinalIgnoreCase))
                .Select(x => ctx.PathOf(x.Request, x.FolderChain))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Wires dirty tracking, ghost rows and live previews. Called from the constructor
    /// after the shared services are assigned.</summary>
    private void InitializeScriptless(RuntimeVariableStore? runtimeStore, LastResponseCache? responseCache)
    {
        _runtimeStore = runtimeStore ?? new RuntimeVariableStore();
        _responseCache = responseCache ?? new LastResponseCache();
        _runtimeStore.Changed += (_, _) =>
        {
            VariablesSnapshotChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(RuntimeVariables));
        };

        WireScriptlessRows(Captures, () => new CaptureRow(), nameof(CapturesCount));
        WireScriptlessRows(Assertions, () => new AssertionRow(), nameof(AssertionsCount));
        WireScriptlessRows(ChainLinks, () => new ChainLinkRow(), nameof(ChainLinksCount));
        ChainSteps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChainSteps));

        _loading = true;
        try { EnsureScriptlessGhostRows(); }
        finally { _loading = false; }
    }

    private void WireScriptlessRows<T>(ObservableCollection<T> rows, Func<T> factory, string countProperty)
        where T : ScriptlessRowBase
    {
        void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not T row || !row.IsDataProperty(e.PropertyName)) return;
            if (!_loading) IsDirty = true;
            if (e.PropertyName == nameof(ScriptlessRowBase.IsBlank))
            {
                OnPropertyChanged(countProperty);
                OnPropertyChanged(nameof(ScriptlessCount));
            }
            if (!_loading) RefreshPreview(row);
        }

        rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (T r in e.NewItems) r.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null) foreach (T r in e.OldItems) r.PropertyChanged -= OnRowChanged;
            if (!_loading) IsDirty = true;
            OnPropertyChanged(countProperty);
            OnPropertyChanged(nameof(ScriptlessCount));
        };
        KvAutoAppend.Wire(rows, factory, r => r.IsBlank, () => _loading);
    }

    private void EnsureScriptlessGhostRows()
    {
        KvAutoAppend.EnsureTrailingBlank(Captures, () => new CaptureRow(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(Assertions, () => new AssertionRow(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(ChainLinks, () => new ChainLinkRow(), r => r.IsBlank);
    }

    // ------------------------------------------------------------------ load / save

    /// <summary>Populates the scriptless rows from a request. Call inside the <c>_loading</c> guard.</summary>
    private void LoadScriptless(RequestItem item)
    {
        Captures.Clear();
        foreach (var p in item.PostResponseVars) Captures.Add(CaptureRow.FromStorage(p, CaptureScope.Runtime));
        foreach (var p in item.PostResponseEnvVars) Captures.Add(CaptureRow.FromStorage(p, CaptureScope.Environment));
        foreach (var p in item.PostResponseGlobalVars) Captures.Add(CaptureRow.FromStorage(p, CaptureScope.Global));
        Assertions.Clear();
        foreach (var a in item.Assertions) Assertions.Add(AssertionRow.FromStorage(a));
        ChainLinks.Clear();
        foreach (var l in item.ChainLinks) ChainLinks.Add(ChainLinkRow.FromStorage(l));
        ChainSteps.Clear();
        _lastResponseSnapshot = null;
        IsTestsScriptExpanded = !string.IsNullOrWhiteSpace(item.Tests);
        EnsureScriptlessGhostRows();
        RefreshAllPreviews();
    }

    private List<KvPair> CapturesFor(CaptureScope scope) =>
        Captures.Where(c => !c.IsBlank && c.Scope == scope).Select(c => c.ToKvPair()).ToList();

    private List<KvPair> AssertionPairs() =>
        Assertions.Where(a => !a.IsBlank).Select(a => a.ToKvPair()).ToList();

    private List<KvPair> ChainLinkPairs() =>
        ChainLinks.Where(l => !l.IsBlank).Select(l => l.ToKvPair()).ToList();

    /// <summary>Overlays the scriptless rows onto a request snapshot.</summary>
    private RequestItem WithScriptless(RequestItem item) => item with
    {
        PostResponseVars = CapturesFor(CaptureScope.Runtime),
        PostResponseEnvVars = CapturesFor(CaptureScope.Environment),
        PostResponseGlobalVars = CapturesFor(CaptureScope.Global),
        Assertions = AssertionPairs(),
        ChainLinks = ChainLinkPairs(),
    };

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void AddCapture() => Captures.Add(new CaptureRow());

    [RelayCommand]
    private void RemoveCapture(CaptureRow? row) { if (row is not null) Captures.Remove(row); }

    [RelayCommand]
    private void AddAssertion() => Assertions.Add(new AssertionRow());

    [RelayCommand]
    private void RemoveAssertion(AssertionRow? row) { if (row is not null) Assertions.Remove(row); }

    [RelayCommand]
    private void AddChainLink() => ChainLinks.Add(new ChainLinkRow());

    [RelayCommand]
    private void RemoveChainLink(ChainLinkRow? row) { if (row is not null) ChainLinks.Remove(row); }

    [RelayCommand]
    private void ClearRuntimeVariables() => _runtimeStore.Clear();

    /// <summary>Adds a capture row (used by the response pane's "Capture as variable" gestures).
    /// Inserts before the ghost row and returns the new row.</summary>
    public CaptureRow AddCaptureFromResponse(string name, ResponseSource source, string pathText,
        CaptureScope scope = CaptureScope.Runtime)
    {
        var row = new CaptureRow { Name = UniqueCaptureName(name), Source = source, PathText = pathText, Scope = scope };
        InsertBeforeGhost(Captures, row);
        RefreshPreview(row);
        return row;
    }

    /// <summary>Adds an assertion row (used by the response pane's "Assert…" gestures).</summary>
    public AssertionRow AddAssertionFromResponse(ResponseSource source, string pathText, string operatorToken, string expected)
    {
        var row = new AssertionRow
        {
            Source = source,
            PathText = pathText,
            Operator = AssertionOperators.Find(operatorToken) ?? AssertionOperators.All[0],
            Expected = expected,
        };
        InsertBeforeGhost(Assertions, row);
        RefreshPreview(row);
        return row;
    }

    /// <summary>Seeds assertions from the last response: status, content type, a generous
    /// response-time budget, and a type check for each top-level JSON field.</summary>
    [RelayCommand]
    private void GenerateAssertionsFromResponse()
    {
        var snap = CurrentResponseSnapshot();
        if (snap is null) return;

        bool Exists(ResponseSource s, string path) =>
            Assertions.Any(a => !a.IsBlank && a.Source == s && string.Equals(a.PathText, path, StringComparison.Ordinal));

        if (!Exists(ResponseSource.Status, string.Empty))
            AddAssertionFromResponse(ResponseSource.Status, string.Empty, "eq", snap.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (snap.ContentType is { Length: > 0 } ct && !Exists(ResponseSource.Header, "Content-Type"))
            AddAssertionFromResponse(ResponseSource.Header, "Content-Type", "contains", ct.Split(';')[0].Trim());
        if (!Exists(ResponseSource.ResponseTime, string.Empty))
        {
            var budget = Math.Max(500, (int)Math.Ceiling(snap.ElapsedMilliseconds * 2 / 100.0) * 100);
            AddAssertionFromResponse(ResponseSource.ResponseTime, string.Empty, "lt", budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (snap.Json is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var (key, value) in obj.Take(12))
            {
                var path = FriendlyPath.IsPlainKey(key) ? key : "[" + ResponseExpression.Quote(key) + "]";
                if (Exists(ResponseSource.Body, path)) continue;
                var op = ResolvedValue.Of(value).TypeName switch
                {
                    "string" => "isString",
                    "number" => "isNumber",
                    "boolean" => "isBoolean",
                    "array" => "isArray",
                    "object" => "isObject",
                    _ => "isDefined",
                };
                AddAssertionFromResponse(ResponseSource.Body, path, op, string.Empty);
            }
        }
        else if (snap.Json is System.Text.Json.Nodes.JsonArray && !Exists(ResponseSource.Body, string.Empty))
        {
            AddAssertionFromResponse(ResponseSource.Body, string.Empty, "isArray", string.Empty);
        }
    }

    private string UniqueCaptureName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "value" : name.Trim();
        var candidate = baseName;
        for (var i = 2; Captures.Any(c => string.Equals(c.Name, candidate, StringComparison.Ordinal)); i++)
            candidate = baseName + i;
        return candidate;
    }

    private static void InsertBeforeGhost<T>(ObservableCollection<T> rows, T row) where T : ScriptlessRowBase
    {
        var index = rows.Count > 0 && rows[^1].IsBlank ? rows.Count - 1 : rows.Count;
        rows.Insert(index, row);
    }

    // ------------------------------------------------------------------ previews

    /// <summary>The last response as a snapshot, or null before the first Send.</summary>
    public ResponseSnapshot? CurrentResponseSnapshot() =>
        _lastResponseSnapshot ?? (HasResponse
            ? new ResponseSnapshot(ResponseStatusCode, ResponseStatusText, ResponseBody, ResponseElapsedMilliseconds,
                ResponseHeaders.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)), Url)
            : null);

    private ResponseSnapshot? _lastResponseSnapshot;

    private IReadOnlyDictionary<string, string> PreviewVariables() => ResolveCurrentVariables();

    private ResponseSnapshot? _suggestionsFor;
    private readonly Dictionary<ResponseSource, IReadOnlyList<string>> _suggestionCache = new();

    /// <summary>Path suggestions per source, computed once per response.</summary>
    private IReadOnlyList<string> SuggestionsFor(ResponseSource source, ResponseSnapshot? snap)
    {
        if (!ReferenceEquals(snap, _suggestionsFor)) { _suggestionCache.Clear(); _suggestionsFor = snap; }
        if (!_suggestionCache.TryGetValue(source, out var list))
            _suggestionCache[source] = list = ResponsePathSuggester.For(source, snap);
        return list;
    }

    /// <summary>Recomputes every row's preview against the last response.</summary>
    public void RefreshAllPreviews()
    {
        foreach (var c in Captures) RefreshPreview(c);
        foreach (var a in Assertions) RefreshPreview(a);
    }

    private void RefreshPreview(ScriptlessRowBase row)
    {
        var snap = CurrentResponseSnapshot();
        var source = row switch { CaptureRow c => c.Source, AssertionRow a => a.Source, _ => (ResponseSource?)null };
        if (source is { } src) row.PathSuggestions = SuggestionsFor(src, snap);
        if (row.IsBlank) { row.ClearResult(); return; }
        if (snap is null) { row.ClearResult(); return; }

        // JavaScript expressions are only evaluated on Send — previews stay native (no engine spin-up per keystroke).
        var vars = PreviewVariables();
        switch (row)
        {
            case CaptureRow c:
            {
                if (c.Source == ResponseSource.Script) { c.SetResult(RowResultKind.None, "evaluated on Send"); return; }
                var outcome = CaptureEvaluator.EvaluateOne(c.ToRule() with { Enabled = true }, snap,
                    interpolate: s => Interpolator.Resolve(s, vars),
                    lookupVar: n => vars.TryGetValue(n, out var v) ? v : null);
                ApplyCaptureOutcome(c, outcome, preview: true);
                break;
            }
            case AssertionRow a:
            {
                if (a.Source == ResponseSource.Script) { a.SetResult(RowResultKind.None, "evaluated on Send"); return; }
                var o = AssertionEvaluator.Evaluate(a.ExpressionStorage, a.SpecStorage, snap,
                    interpolate: s => Interpolator.Resolve(s, vars),
                    lookupVar: n => vars.TryGetValue(n, out var v) ? v : null);
                ApplyAssertionOutcome(a, o);
                break;
            }
        }
    }

    private static void ApplyCaptureOutcome(CaptureRow row, CaptureEvaluator.Outcome outcome, bool preview)
    {
        if (outcome.Error is not null) row.SetResult(RowResultKind.Error, outcome.Error);
        else if (!outcome.Value.Exists) row.SetResult(RowResultKind.Missing, outcome.Applied ? "no match → \"\"" : "no match — skipped");
        else if (!outcome.Applied) row.SetResult(RowResultKind.Missing, "empty — skipped");
        else row.SetResult(RowResultKind.Ok, (preview ? "→ " : "✓ ") + outcome.Value.ToDisplayString(80));
    }

    private static void ApplyAssertionOutcome(AssertionRow row, AssertionEvaluator.Outcome o) =>
        row.SetResult(o.Passed ? RowResultKind.Ok : RowResultKind.Error,
            o.Passed ? "✓ " + o.Actual.ToDisplayString(60) : o.FailureMessage);

    // ------------------------------------------------------------------ send-time steps

    /// <summary>The collection-relative path of this request (for the response cache and
    /// cycle detection), or null for drafts.</summary>
    private string? SelfChainPath(ChainContext ctx) =>
        string.IsNullOrEmpty(SourcePath) ? null : ctx.PathOf(new RequestItem { Name = SourcePath, SourcePath = SourcePath }, _parentFolderChain);

    /// <summary>Runs linked requests before Send. Returns an error message when a link failed
    /// (the Send must stop), otherwise null. Variables they produce land in the runtime store
    /// and the environment exactly as if those requests had been sent by hand.</summary>
    private async Task<string?> RunChainLinksAsync(RequestComposition.Composed composed, CancellationToken ct)
    {
        ChainSteps.Clear();
        if (composed.ChainLinks.Count == 0 || _parentCollection is null) return null;

        var ctx = new ChainContext(_parentCollection, null, _responseCache);
        var self = SelfChainPath(ctx);
        if (self is not null) ctx = ctx.Push(self);

        var chain = await ChainExecutor.RunAsync(composed.ChainLinks, ctx,
            EnvironmentVariables, _runtimeStore.Snapshot(), _executor, _scriptHost,
            CurrentWorkspaceContext(), ct).ConfigureAwait(true);

        foreach (var s in chain.Steps) ChainSteps.Add(s);

        if (chain.RuntimeVariables.Count > 0)
        {
            var origin = chain.Steps.Where(s => s.Ran).Select(s => s.Name).LastOrDefault();
            _runtimeStore.SetMany(chain.RuntimeVariables, origin,
                n => chain.Steps.FirstOrDefault(s => s.Captured.ContainsKey(n))?.Path);
        }
        RaiseEnvVarMutations(EnvironmentVariables, MergeVariables(EnvironmentVariables, chain.EnvVariables));
        RaiseGlobalVarMutations(chain.GlobalVariables);
        return chain.Error;
    }

    /// <summary>Evaluates capture rows after the response; writes runtime values to the session
    /// store and raises env / global mutations. Returns the runtime values so the post-response
    /// script and assertions see them.</summary>
    private IReadOnlyDictionary<string, string> RunCaptures(ResponseSnapshot snap, IReadOnlyDictionary<string, string> vars,
        CancellationToken ct)
    {
        var active = Captures.Where(c => c.IsActive).ToList();
        if (active.Count == 0) return new Dictionary<string, string>();

        var result = ScriptlessSteps.RunCaptures(active.Select(c => c.ToRule()), snap, vars, _scriptHost, ct);
        for (var i = 0; i < result.Outcomes.Count && i < active.Count; i++)
            ApplyCaptureOutcome(active[i], result.Outcomes[i], preview: false);

        var origin = string.IsNullOrEmpty(SourcePath) ? "this request" : Path.GetFileNameWithoutExtension(SourcePath);
        if (result.Runtime.Count > 0)
            _runtimeStore.SetMany(result.Runtime, origin,
                n => result.Outcomes.FirstOrDefault(o => o.Name == n)?.Expression);
        if (result.Environment.Count > 0)
            RaiseEnvVarMutations(EnvironmentVariables, MergeVariables(EnvironmentVariables, result.Environment));
        RaiseGlobalVarMutations(result.Global);
        return result.Runtime;
    }

    /// <summary>Evaluates assertion rows plus inherited (folder / collection) assertions.
    /// Request rows get their ✓/✗ chip; every outcome becomes a Test Results row.</summary>
    private IReadOnlyList<TestResultRow> RunAssertions(RequestComposition.Composed composed, ResponseSnapshot snap,
        IReadOnlyDictionary<string, string> vars, CancellationToken ct)
    {
        if (composed.Assertions.Count == 0) return Array.Empty<TestResultRow>();
        var outcomes = ScriptlessSteps.RunAssertions(composed.Assertions, snap, vars, _scriptHost, ct);

        // The request's own rows are the tail of the composed list (collection → folders → request).
        var own = Assertions.Where(a => a.IsActive).ToList();
        var offset = outcomes.Count - own.Count;
        for (var i = 0; i < own.Count && offset + i >= 0; i++)
            ApplyAssertionOutcome(own[i], outcomes[offset + i]);

        return outcomes.Select(o => new TestResultRow(o.Name, o.Passed, o.FailureMessage, o.DurationMs, IsAssertion: true)).ToList();
    }

    /// <summary>Records this response for "when-stale" links and live previews.</summary>
    private void RememberResponse(ResponseSnapshot snap)
    {
        _lastResponseSnapshot = snap;
        foreach (var c in Captures) c.PathSuggestions = SuggestionsFor(c.Source, snap);
        foreach (var a in Assertions) a.PathSuggestions = SuggestionsFor(a.Source, snap);
        if (_parentCollection is null || string.IsNullOrEmpty(SourcePath)) return;
        var ctx = new ChainContext(_parentCollection, null, _responseCache);
        if (SelfChainPath(ctx) is { } path) _responseCache.Record(path, snap);
    }

    /// <summary>Raises a global-scope mutation (captures with Global scope / linked requests).</summary>
    private void RaiseGlobalVarMutations(IReadOnlyDictionary<string, string> updated)
    {
        if (updated.Count == 0 || EnvironmentVariablesMutated is null) return;
        EnvironmentVariablesMutated.Invoke(this,
            new EnvVarMutationEventArgs(updated, Array.Empty<string>()) { TargetGlobal = true });
    }

    /// <summary>Provenance label for values set from this request's scripts.</summary>
    private string ScriptOrigin() =>
        string.IsNullOrEmpty(SourcePath) ? "this request" : Path.GetFileNameWithoutExtension(SourcePath);

    /// <summary>Variables a script set via <c>bru.setVar</c>: entries of <paramref name="after"/>
    /// that are new or changed relative to the bag the script was seeded with.</summary>
    private static Dictionary<string, string> ScriptRuntimeDelta(
        IReadOnlyDictionary<string, string> seeded, IReadOnlyDictionary<string, string> after)
    {
        var delta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in after)
            if (!seeded.TryGetValue(k, out var old) || !string.Equals(old, v, StringComparison.Ordinal))
                delta[k] = v;
        return delta;
    }
}
