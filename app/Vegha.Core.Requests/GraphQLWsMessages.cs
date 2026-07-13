namespace Vegha.Core.Requests;

/// <summary>Kind of an event surfaced by <see cref="GraphQLWsClient"/>.</summary>
public enum GraphQLWsEventKind
{
    /// <summary>An execution result frame (transport-ws <c>next</c> / legacy <c>data</c>).</summary>
    Next,
    /// <summary>A GraphQL error frame for the subscription.</summary>
    Error,
    /// <summary>The server completed the subscription (no more events).</summary>
    Complete,
    /// <summary>Connection lifecycle noise (connected, closed, ping) for the timeline.</summary>
    System,
}

/// <summary>One protocol event. <see cref="PayloadJson"/> carries the frame's payload as raw
/// JSON text (the execution result for <see cref="GraphQLWsEventKind.Next"/>).</summary>
public sealed record GraphQLWsEvent(
    GraphQLWsEventKind Kind,
    string? SubscriptionId,
    string PayloadJson,
    DateTimeOffset Timestamp);

/// <summary>The two wire dialects. Message-type tokens differ; semantics map 1:1 for the
/// client's purposes.</summary>
public enum GraphQLWsDialect
{
    /// <summary>graphql-transport-ws (graphql-ws npm lib): connection_init/ack,
    /// subscribe/next/error/complete, ping/pong.</summary>
    TransportWs,
    /// <summary>Legacy subscriptions-transport-ws: connection_init/ack, start/data/error/
    /// complete/stop, ka keep-alives.</summary>
    LegacyWs,
}