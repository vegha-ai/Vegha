using System.Diagnostics;
using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Flow;

/// <summary>Top-level driver for the Collection Runner. Owns iteration scheduling
/// (Parallel.ForEachAsync across N workers) and reuses the existing
/// <see cref="CollectionRunner"/> tree-walker for the within-iteration sequential execution.
/// The actual HTTP work happens inside the caller-supplied executor delegate — keeping the
/// orchestrator UI-free and trivially unit-testable with fake executors.</summary>
public static class CollectionRunOrchestrator
{
    /// <summary>One per-request executor invocation. Receives the iteration index, the
    /// request item, its folder chain, the iteration's overlay vars, and a cancellation
    /// token. Must return a <see cref="RequestRunResult"/> describing what happened —
    /// success, failure with status code + tests counts, or an error.</summary>
    public delegate Task<RequestRunResult> RequestExecutor(
        int iterationIndex,
        RequestItem request,
        IReadOnlyList<Folder> folderChain,
        IReadOnlyDictionary<string, string> iterationVariables,
        CancellationToken cancellationToken);

    /// <summary>Aggregate run summary returned at the end. Mirrors the counts surfaced
    /// in <see cref="RunCompleted"/> so callers that ignore progress events can still
    /// render a final status line.</summary>
    public sealed record RunSummary(
        int Passed,
        int Failed,
        int Errored,
        int Skipped,
        int Canceled,
        long DurationMs,
        bool WasCanceled,
        IReadOnlyList<RequestRunResult> Results);

    /// <summary>Runs the collection per <paramref name="options"/>. Honors cancellation
    /// promptly between requests; an in-flight request finishes (the executor delegate is
    /// responsible for passing the token along). The progress sink may be null for
    /// fire-and-forget callers.</summary>
    public static async Task<RunSummary> RunAsync(
        RunnerOptions options,
        RequestExecutor executor,
        IProgress<RunnerEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new System.Collections.Concurrent.ConcurrentBag<RequestRunResult>();

        var totalRequests = CountRequests(options.Collection) * options.EffectiveIterations;
        progress?.Report(new RunStarted(totalRequests, options.EffectiveIterations, options.Workers));

        var parallelism = Math.Max(1, options.Workers);
        var iterations = Enumerable.Range(0, options.EffectiveIterations);
        var wasCanceled = false;

        // StopOnError trips this linked source on the first failed/errored row without
        // tripping the caller's token — so we can distinguish an early stop (WasCanceled=false)
        // from a user cancel (WasCanceled=true) below.
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Pre-resolve the explicit run order (if any) against the tree once — the same plan is
        // reused every iteration. Null when running in plain tree-walk mode.
        var orderedPlan = options.OrderedRequestNames is { Count: > 0 }
            ? BuildOrderedPlan(options.Collection, options.OrderedRequestNames)
            : null;

        // Executes one request with the full progress / error / stop-on-error / delay logic.
        // Shared by both the tree-walk and explicit-order paths so the two never drift.
        async Task<RequestRunResult> RunOneAsync(
            int iterIndex, RequestItem req, IReadOnlyList<Folder> chain,
            IReadOnlyDictionary<string, string> iterVars, CancellationToken c)
        {
            if (c.IsCancellationRequested)
                return RequestRunResult.Canceled(req);

            progress?.Report(new RequestStarted(iterIndex, req.Name, req.Method, req.Url));
            RequestRunResult result;
            try
            {
                result = await executor(iterIndex, req, chain, iterVars, c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (c.IsCancellationRequested)
            {
                result = RequestRunResult.Canceled(req);
            }
            catch (Exception ex)
            {
                result = new RequestRunResult(
                    req.Name, req.Method, req.Url,
                    StatusCode: 0, ElapsedMs: 0, Succeeded: false,
                    ErrorMessage: ex.Message,
                    Status: RequestRunStatus.Errored,
                    Kind: req.Kind);
            }

            progress?.Report(new RequestCompleted(iterIndex, result));

            // Stop-on-error: cancel the shared source so the rest of the run unwinds.
            if (options.StopOnError
                && result.Status is RequestRunStatus.Failed or RequestRunStatus.Errored)
            {
                try { stopCts.Cancel(); } catch { /* already disposed/canceled */ }
            }

            if (options.DelayBetweenRequestsMs > 0
                && result.Status != RequestRunStatus.Skipped
                && !c.IsCancellationRequested)
            {
                try { await Task.Delay(options.DelayBetweenRequestsMs, c).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* swallowed; loop will exit */ }
            }
            return result;
        }

        try
        {
            await Parallel.ForEachAsync(
                iterations,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = stopCts.Token,
                },
                async (iterIndex, ct) =>
                {
                    var iterSw = Stopwatch.StartNew();
                    progress?.Report(new IterationStarted(iterIndex));

                    var iterVars = options.DataSource?.GetRow(iterIndex)
                                   ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

                    if (orderedPlan is not null)
                    {
                        // Explicit run order: execute exactly the planned requests, in order.
                        foreach (var (req, chain) in orderedPlan)
                        {
                            if (ct.IsCancellationRequested) break;
                            results.Add(await RunOneAsync(iterIndex, req, chain, iterVars, ct).ConfigureAwait(false));
                        }
                    }
                    else
                    {
                        var selected = options.SelectedRequestNames;
                        var iterResults = await CollectionRunner.RunAsync(
                            options.Collection,
                            executeAsync: (req, chain, c) =>
                                selected is not null && selected.Count > 0 && !selected.Contains(req.Name)
                                    ? Task.FromResult(RequestRunResult.Skipped(req))
                                    : RunOneAsync(iterIndex, req, chain, iterVars, c),
                            cancellationToken: ct).ConfigureAwait(false);
                        foreach (var r in iterResults) results.Add(r);
                    }

                    progress?.Report(new IterationCompleted(iterIndex, iterSw.ElapsedMilliseconds));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cancel trips the caller's token; stop-on-error trips only stopCts. Only the
            // former counts as "canceled" in the summary — an early stop is a completed run.
            wasCanceled = cancellationToken.IsCancellationRequested;
        }

        var resultList = results.ToList();
        var passed = resultList.Count(r => r.Status == RequestRunStatus.Passed);
        var failed = resultList.Count(r => r.Status == RequestRunStatus.Failed);
        var errored = resultList.Count(r => r.Status == RequestRunStatus.Errored);
        var skipped = resultList.Count(r => r.Status == RequestRunStatus.Skipped);
        var canceled = resultList.Count(r => r.Status == RequestRunStatus.Canceled);

        progress?.Report(new RunCompleted(passed, failed, errored, skipped, canceled,
            sw.ElapsedMilliseconds, wasCanceled));

        return new RunSummary(passed, failed, errored, skipped, canceled,
            sw.ElapsedMilliseconds, wasCanceled, resultList);
    }

    /// <summary>Stable per-request identity for run ordering: the folder path plus the request
    /// name (NUL-separated). Disambiguates same-named requests in different folders — critical
    /// when a collection has, say, a "Request 1" under many endpoint folders. For a root-level
    /// request (empty chain) the key is just the name, so flat collections keep name identity.</summary>
    public static string RequestKey(IReadOnlyList<Folder> chain, RequestItem item) =>
        RequestKey(chain.Select(f => f.Name), item.Name);

    /// <inheritdoc cref="RequestKey(IReadOnlyList{Folder}, RequestItem)"/>
    public static string RequestKey(IEnumerable<string> folderNames, string name)
    {
        var sb = new StringBuilder();
        foreach (var f in folderNames) { sb.Append(f); sb.Append('\n'); }
        sb.Append(name);
        return sb.ToString();
    }

    /// <summary>Resolves an explicit run order into concrete (request, folder-chain) pairs.
    /// Walks the collection once to map each request's <see cref="RequestKey(IReadOnlyList{Folder}, RequestItem)"/>
    /// to its item + folder chain, then emits them in <paramref name="orderedKeys"/> order.
    /// Keys with no match in the tree are silently dropped.</summary>
    private static List<(RequestItem Request, IReadOnlyList<Folder> Chain)> BuildOrderedPlan(
        Collection collection, IReadOnlyList<string> orderedKeys)
    {
        var byKey = new Dictionary<string, (RequestItem, IReadOnlyList<Folder>)>(StringComparer.Ordinal);

        void Index(IList<RequestItem> requests, IList<Folder> folders, List<Folder> chain)
        {
            foreach (var r in requests)
            {
                var key = RequestKey(chain, r);
                if (!byKey.ContainsKey(key)) byKey[key] = (r, chain.ToArray());
            }
            foreach (var f in folders)
            {
                chain.Add(f);
                Index(f.Requests, f.Folders, chain);
                chain.RemoveAt(chain.Count - 1);
            }
        }

        Index(collection.Requests, collection.Folders, new List<Folder>());

        var plan = new List<(RequestItem, IReadOnlyList<Folder>)>(orderedKeys.Count);
        foreach (var key in orderedKeys)
            if (byKey.TryGetValue(key, out var hit)) plan.Add(hit);
        return plan;
    }

    /// <summary>Count of request leaves in a collection (root + all nested folders).</summary>
    public static int CountRequests(Collection collection)
    {
        var count = collection.Requests.Count;
        foreach (var f in collection.Folders) count += CountFolderRequests(f);
        return count;
    }

    private static int CountFolderRequests(Folder folder)
    {
        var count = folder.Requests.Count;
        foreach (var f in folder.Folders) count += CountFolderRequests(f);
        return count;
    }
}
