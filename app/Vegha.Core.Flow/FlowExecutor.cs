using System.Threading.Channels;

namespace Vegha.Core.Flow;

/// <summary>One trace event from a flow run — useful for the results panel.</summary>
public sealed record FlowEvent(
    string NodeId,
    string Label,
    DateTimeOffset Timestamp,
    FlowEventKind Kind,
    string? Detail);

public enum FlowEventKind
{
    NodeStarted,
    NodeCompleted,
    NodeFailed,
    Skipped,
    EdgeFollowed,
}

/// <summary>Per-node delegate for the runtime — the host wires Request to its
/// HttpExecutor, Script to JintHost, Branch to an expression evaluator, etc. The
/// executor calls into these via the dispatcher.</summary>
public interface IFlowNodeRunner
{
    Task<FlowNodeResult> RunAsync(FlowNode node, FlowExecutionState state, CancellationToken cancellationToken);
}

public sealed record FlowNodeResult(bool Succeeded, string? Detail, string? PreferredEdgeLabel);

/// <summary>Mutable shared state across one run — variables a Script node sets are
/// visible to downstream nodes. Cleared between runs.</summary>
public sealed class FlowExecutionState
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Executes a <see cref="FlowDefinition"/> by walking from Start, calling the matching
/// runner for each node, and following edges. Branch nodes return a preferred edge
/// label; Parallel nodes fan out across all outgoing edges concurrently. Cancellation
/// on the supplied token aborts the run after the in-flight node returns.
/// </summary>
public sealed class FlowExecutor
{
    private readonly IReadOnlyDictionary<FlowNodeKind, IFlowNodeRunner> _runners;

    public FlowExecutor(IReadOnlyDictionary<FlowNodeKind, IFlowNodeRunner> runners)
    {
        _runners = runners;
    }

    public async Task<FlowRunResult> RunAsync(FlowDefinition flow, CancellationToken cancellationToken = default)
    {
        var state = new FlowExecutionState();
        var events = new List<FlowEvent>();
        var nodesById = flow.Nodes.ToDictionary(n => n.Id);
        var edgesByFrom = flow.Edges.GroupBy(e => e.FromNodeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var start = flow.Nodes.FirstOrDefault(n => n.Kind == FlowNodeKind.Start);
        if (start is null) throw new InvalidOperationException("Flow has no Start node");

        var visiting = new HashSet<string>();
        var ok = await VisitAsync(start, nodesById, edgesByFrom, state, events, visiting, cancellationToken)
            .ConfigureAwait(false);

        return new FlowRunResult(ok, state, events);
    }

    private async Task<bool> VisitAsync(
        FlowNode node,
        IReadOnlyDictionary<string, FlowNode> nodesById,
        IReadOnlyDictionary<string, List<FlowEdge>> edgesByFrom,
        FlowExecutionState state,
        List<FlowEvent> events,
        HashSet<string> visiting,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (!visiting.Add(node.Id))
            throw new InvalidOperationException($"Cycle detected at node '{node.Id}'");
        try
        {
            events.Add(new FlowEvent(node.Id, node.Label, DateTimeOffset.UtcNow, FlowEventKind.NodeStarted, null));

            FlowNodeResult result;
            if (node.Kind == FlowNodeKind.End)
            {
                events.Add(new FlowEvent(node.Id, node.Label, DateTimeOffset.UtcNow, FlowEventKind.NodeCompleted, null));
                return true;
            }

            if (_runners.TryGetValue(node.Kind, out var runner))
            {
                result = await runner.RunAsync(node, state, ct).ConfigureAwait(false);
            }
            else
            {
                // Default: pass through.
                result = new FlowNodeResult(true, null, null);
            }

            events.Add(new FlowEvent(node.Id, node.Label, DateTimeOffset.UtcNow,
                result.Succeeded ? FlowEventKind.NodeCompleted : FlowEventKind.NodeFailed, result.Detail));
            if (!result.Succeeded) return false;

            if (!edgesByFrom.TryGetValue(node.Id, out var outgoing) || outgoing.Count == 0)
                return true;

            // Branch nodes: follow the edge whose label matches the runner's preferred label.
            if (node.Kind == FlowNodeKind.Branch && result.PreferredEdgeLabel is not null)
            {
                var match = outgoing.FirstOrDefault(e => string.Equals(e.Label, result.PreferredEdgeLabel, StringComparison.Ordinal))
                            ?? outgoing.FirstOrDefault(e => string.Equals(e.Label, "default", StringComparison.Ordinal))
                            ?? outgoing.First();
                events.Add(new FlowEvent(node.Id, node.Label, DateTimeOffset.UtcNow, FlowEventKind.EdgeFollowed, match.Label));
                return await VisitAsync(nodesById[match.ToNodeId], nodesById, edgesByFrom, state, events, visiting, ct);
            }

            // Parallel nodes: fan out across all outgoing edges concurrently.
            if (node.Kind == FlowNodeKind.Parallel)
            {
                var tasks = outgoing.Select(e =>
                    VisitAsync(nodesById[e.ToNodeId], nodesById, edgesByFrom, state, events, new HashSet<string>(visiting), ct));
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.All(r => r);
            }

            // Default: linear flow — follow the first outgoing edge.
            var next = outgoing[0];
            events.Add(new FlowEvent(node.Id, node.Label, DateTimeOffset.UtcNow, FlowEventKind.EdgeFollowed, next.Label));
            return await VisitAsync(nodesById[next.ToNodeId], nodesById, edgesByFrom, state, events, visiting, ct);
        }
        finally
        {
            visiting.Remove(node.Id);
        }
    }
}

public sealed record FlowRunResult(bool Succeeded, FlowExecutionState State, IReadOnlyList<FlowEvent> Events);
