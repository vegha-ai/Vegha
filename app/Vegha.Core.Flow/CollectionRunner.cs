using Vegha.Core.Domain;

namespace Vegha.Core.Flow;

/// <summary>Status of one request in a collection run. Distinct from "succeeded" because
/// the runner needs to render skipped (selection-filtered) and canceled rows differently
/// from a successful 200 or a failed 500.</summary>
public enum RequestRunStatus
{
    Passed,
    Failed,
    Errored,
    Skipped,
    Canceled,
}

/// <summary>One request's outcome in a collection run.</summary>
public sealed record RequestRunResult(
    string Name,
    string Method,
    string Url,
    int StatusCode,
    long ElapsedMs,
    bool Succeeded,
    string? ErrorMessage,
    RequestRunStatus Status = RequestRunStatus.Passed,
    int PassedTests = 0,
    int FailedTests = 0,
    RequestKind Kind = RequestKind.Http,
    /// <summary>Total response size in bytes (headers + body). Shown in the Postman-style
    /// result row and rolled up into the run summary. Zero for skipped/canceled/errored rows.</summary>
    long ResponseSizeBytes = 0,
    /// <summary>Captured response/request payload for the detail pane. Null for rows that never
    /// produced an HTTP exchange (skipped, canceled, transport-errored) — the pane shows nothing.
    /// Not captured when the run disables response persistence.</summary>
    RunResultDetail? Detail = null,
    /// <summary>Folder path of the request (e.g. "Auth / Login"), for disambiguating same-named
    /// requests in the results list. Empty for root-level requests.</summary>
    string FolderPath = "")
{
    /// <summary>True for GraphQL requests — result rows show the GraphQL mark instead of
    /// the method label.</summary>
    public bool IsGraphQL => Kind == RequestKind.GraphQL;

    /// <summary>Total tests recorded for this request (passed + failed).</summary>
    public int TotalTests => PassedTests + FailedTests;

    /// <summary>Factory for a skipped row (selection filter excluded the request). Tests
    /// counts are zero; rendering treats it as a neutral row, not pass or fail.</summary>
    public static RequestRunResult Skipped(RequestItem item) =>
        new(item.Name, item.Method, item.Url,
            StatusCode: 0, ElapsedMs: 0, Succeeded: false,
            ErrorMessage: null, Status: RequestRunStatus.Skipped, Kind: item.Kind);

    /// <summary>Factory for a canceled row (token tripped mid-run).</summary>
    public static RequestRunResult Canceled(RequestItem item) =>
        new(item.Name, item.Method, item.Url,
            StatusCode: 0, ElapsedMs: 0, Succeeded: false,
            ErrorMessage: "Canceled", Status: RequestRunStatus.Canceled, Kind: item.Kind);
}

/// <summary>Captured request/response payload + per-test + console detail for one run row.
/// Feeds the Postman-style detail split pane (Response / Headers / Request tabs). Lives in
/// Core.Flow with primitive-only fields so the flow layer stays free of a Scripting dependency;
/// the executor maps <c>TestOutcome</c>/<c>ConsoleMessage</c> into these on the way out.</summary>
public sealed record RunResultDetail(
    string ResponseBody,
    IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
    string? RequestBody,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    IReadOnlyList<RunTestOutcome> Tests,
    IReadOnlyList<RunConsoleMessage> Console);

/// <summary>One assertion outcome inside a run row's detail (name + pass/fail + message).</summary>
public sealed record RunTestOutcome(string Name, bool Passed, string? Message);

/// <summary>One console line captured while a run row executed (level + text).</summary>
public sealed record RunConsoleMessage(string Level, string Text);

/// <summary>
/// Runs every request in a Collection or Folder in tree-order and reports per-request
/// results. Doesn't depend on FlowExecutor — collection runs are linear and don't need
/// the DAG semantics. Caller supplies an executor delegate so this class stays in the
/// Core layer (no HttpExecutor dependency).
/// </summary>
public static class CollectionRunner
{
    /// <summary>Walks the collection top-down (root requests first, then folders depth-first)
    /// and invokes <paramref name="executeAsync"/> for each request. Cancellation aborts
    /// after the in-flight request returns.</summary>
    public static async Task<IReadOnlyList<RequestRunResult>> RunAsync(
        Collection collection,
        Func<RequestItem, IReadOnlyList<Folder>, CancellationToken, Task<RequestRunResult>> executeAsync,
        CancellationToken cancellationToken = default,
        Action<RequestRunResult>? onProgress = null)
    {
        var results = new List<RequestRunResult>();
        var chain = new List<Folder>();
        await VisitAsync(collection.Requests, collection.Folders, chain, executeAsync, results, onProgress, cancellationToken)
            .ConfigureAwait(false);
        return results;
    }

    /// <summary>Same as <see cref="RunAsync"/> but scoped to a single folder + its nested
    /// folders (used when the user clicks "Run" on a folder, not the root).</summary>
    public static async Task<IReadOnlyList<RequestRunResult>> RunFolderAsync(
        Folder folder,
        IReadOnlyList<Folder> outerChain,
        Func<RequestItem, IReadOnlyList<Folder>, CancellationToken, Task<RequestRunResult>> executeAsync,
        CancellationToken cancellationToken = default,
        Action<RequestRunResult>? onProgress = null)
    {
        var results = new List<RequestRunResult>();
        var chain = new List<Folder>(outerChain) { folder };
        await VisitAsync(folder.Requests, folder.Folders, chain, executeAsync, results, onProgress, cancellationToken)
            .ConfigureAwait(false);
        return results;
    }

    private static async Task VisitAsync(
        IList<RequestItem> requests,
        IList<Folder> folders,
        List<Folder> chain,
        Func<RequestItem, IReadOnlyList<Folder>, CancellationToken, Task<RequestRunResult>> executeAsync,
        List<RequestRunResult> sink,
        Action<RequestRunResult>? onProgress,
        CancellationToken ct)
    {
        foreach (var req in requests)
        {
            if (ct.IsCancellationRequested) return;
            var result = await executeAsync(req, chain, ct).ConfigureAwait(false);
            sink.Add(result);
            onProgress?.Invoke(result);
        }
        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) return;
            chain.Add(folder);
            await VisitAsync(folder.Requests, folder.Folders, chain, executeAsync, sink, onProgress, ct)
                .ConfigureAwait(false);
            chain.RemoveAt(chain.Count - 1);
        }
    }
}
