namespace Vegha.Core.Flow;

/// <summary>What kind of step a flow node is. Each kind has its own runtime semantics —
/// Request fires an HTTP call, Script runs a Jint snippet, Branch picks a downstream
/// edge based on a condition expression, etc.</summary>
public enum FlowNodeKind
{
    Start,
    Request,
    Script,
    Branch,
    Parallel,
    Delay,
    End,
}

public sealed record FlowNode(
    string Id,
    FlowNodeKind Kind,
    string Label,
    /// <summary>Per-kind configuration. Request: { requestId }. Script: { source }.
    /// Branch: { condition }. Parallel: { fanout }. Delay: { ms }.</summary>
    IReadOnlyDictionary<string, string> Properties);

public sealed record FlowEdge(
    string FromNodeId,
    string ToNodeId,
    /// <summary>Optional label for branching edges (e.g., "true", "false", "default").</summary>
    string? Label = null);

/// <summary>A directed graph of flow nodes plus edges. Has exactly one Start node;
/// End nodes mark terminal points. Cycles are not allowed at runtime — the executor
/// rejects them on entry.</summary>
public sealed record FlowDefinition(
    string Name,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowEdge> Edges);
