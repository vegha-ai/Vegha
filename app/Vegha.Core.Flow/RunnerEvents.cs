namespace Vegha.Core.Flow;

/// <summary>Progress events emitted by <see cref="CollectionRunOrchestrator"/> through the
/// caller's <see cref="IProgress{T}"/>. The runner UI subscribes and updates its observable
/// collections; consumers that only need the final result can ignore these and just await
/// <see cref="CollectionRunOrchestrator.RunAsync"/>.
///
/// Events fire on the same thread that completed each request — that's a worker pool
/// thread when <c>Workers > 1</c>. UI subscribers must marshal back to the dispatcher.</summary>
public abstract record RunnerEvent;

public sealed record RunStarted(
    int TotalRequests,
    int TotalIterations,
    int Workers) : RunnerEvent;

public sealed record IterationStarted(int Index) : RunnerEvent;

public sealed record RequestStarted(
    int IterationIndex,
    string RequestName,
    string Method,
    string Url) : RunnerEvent;

public sealed record RequestCompleted(
    int IterationIndex,
    RequestRunResult Result) : RunnerEvent;

public sealed record IterationCompleted(
    int Index,
    long DurationMs) : RunnerEvent;

public sealed record RunCompleted(
    int Passed,
    int Failed,
    int Errored,
    int Skipped,
    int Canceled,
    long DurationMs,
    bool WasCanceled) : RunnerEvent;
