using System.Diagnostics;
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

        try
        {
            await Parallel.ForEachAsync(
                iterations,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (iterIndex, ct) =>
                {
                    var iterSw = Stopwatch.StartNew();
                    progress?.Report(new IterationStarted(iterIndex));

                    var iterVars = options.DataSource?.GetRow(iterIndex)
                                   ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
                    var selected = options.SelectedRequestNames;

                    var iterResults = await CollectionRunner.RunAsync(
                        options.Collection,
                        executeAsync: async (req, chain, c) =>
                        {
                            if (c.IsCancellationRequested)
                                return RequestRunResult.Canceled(req);

                            if (selected is not null && selected.Count > 0
                                && !selected.Contains(req.Name))
                            {
                                return RequestRunResult.Skipped(req);
                            }

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
                                    Status: RequestRunStatus.Errored);
                            }

                            progress?.Report(new RequestCompleted(iterIndex, result));

                            if (options.DelayBetweenRequestsMs > 0
                                && result.Status != RequestRunStatus.Skipped
                                && !c.IsCancellationRequested)
                            {
                                try { await Task.Delay(options.DelayBetweenRequestsMs, c).ConfigureAwait(false); }
                                catch (OperationCanceledException) { /* swallowed; loop will exit */ }
                            }
                            return result;
                        },
                        cancellationToken: ct).ConfigureAwait(false);

                    foreach (var r in iterResults) results.Add(r);
                    progress?.Report(new IterationCompleted(iterIndex, iterSw.ElapsedMilliseconds));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCanceled = true;
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
