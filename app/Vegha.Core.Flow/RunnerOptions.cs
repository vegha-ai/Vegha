using Vegha.Core.Domain;

namespace Vegha.Core.Flow;

/// <summary>Configuration for one Collection Runner invocation. Immutable record so the
/// run tab can keep the config alongside the results for a clean re-run.</summary>
public sealed record RunnerOptions(
    /// <summary>The collection whose tree is walked.</summary>
    Collection Collection,
    /// <summary>When non-null and non-empty, only requests whose <see cref="RequestItem.Name"/>
    /// is in this set execute; others emit <c>RequestRunStatus.Skipped</c> rows. Names are
    /// matched ordinally — collisions across folders execute every match. Use stable ids in
    /// a future iteration if collision-correctness becomes important.</summary>
    IReadOnlySet<string>? SelectedRequestNames,
    /// <summary>How many times to walk the tree. Ignored when <see cref="DataSource"/> is set
    /// (the data source's row count drives iteration count). Default 1.</summary>
    int Iterations,
    /// <summary>Max concurrent iterations. 1 = strictly sequential (Bruno parity). Requests
    /// within one iteration always run sequentially regardless of this value.</summary>
    int Workers,
    /// <summary>Milliseconds to <c>Task.Delay</c> between consecutive requests inside one
    /// iteration. Honored by the executor delegate, not the orchestrator.</summary>
    int DelayBetweenRequestsMs,
    /// <summary>Optional per-iteration variable source. Each row becomes the iteration's
    /// <c>iterationVars</c> bag, available to <c>{{var}}</c> interpolation + scripts.</summary>
    IterationDataSource? DataSource,
    /// <summary>Active environment variables (lowest precedence in the var stack).</summary>
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    /// <summary>Off by default — runner runs do NOT pollute the global history pane. Toggle
    /// surfaces a checkbox in the UI for users who want a full audit trail.</summary>
    bool RecordToHistory = false,
    /// <summary>On by default — each run gets a fresh cookie jar so workers don't bleed
    /// auth cookies into each other. Turn off only for runs that need cross-iteration
    /// session continuity.</summary>
    bool IsolatedCookieJarPerRun = true)
{
    /// <summary>Sensible defaults: 1 iteration, sequential, no delay, no data source.</summary>
    public static RunnerOptions Default(Collection collection) =>
        new(collection,
            SelectedRequestNames: null,
            Iterations: 1,
            Workers: 1,
            DelayBetweenRequestsMs: 0,
            DataSource: null,
            EnvironmentVariables: new Dictionary<string, string>());

    /// <summary>Effective number of iterations: data source row count when set, otherwise
    /// the manual count. Always >= 1.</summary>
    public int EffectiveIterations =>
        DataSource is { } ds && ds.RowCount > 0 ? ds.RowCount : Math.Max(1, Iterations);
}
