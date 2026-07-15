using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Vegha.Core.Requests;

/// <summary>
/// GraphQL-over-WebSocket client for subscriptions, wrapping <see cref="WebSocketExecutor"/>.
/// Offers both subprotocols on connect and speaks whichever the server negotiated:
/// <c>graphql-transport-ws</c> (modern; <c>subscribe/next/complete</c>, answers <c>ping</c>
/// with <c>pong</c>) or legacy <c>graphql-ws</c> (<c>start/data/stop</c>, tolerates
/// <c>ka</c> keep-alives). One client per connection; dispose to tear down.
/// </summary>
public sealed class GraphQLWsClient : IAsyncDisposable
{
    public const string TransportWsProtocol = "graphql-transport-ws";
    public const string LegacyWsProtocol = "graphql-ws";

    private readonly TimeSpan _ackTimeout;

    /// <summary><paramref name="ackTimeout"/> defaults to 10 s; tests shorten it.</summary>
    public GraphQLWsClient(TimeSpan? ackTimeout = null) =>
        _ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(10);

    private readonly WebSocketExecutor _ws = new();
    private readonly Channel<GraphQLWsEvent> _events = Channel.CreateUnbounded<GraphQLWsEvent>();
    private readonly TaskCompletionSource _ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pumpTask;
    private int _nextId;

    /// <summary>Protocol events for the UI (Next frames, errors, completion, system notes).</summary>
    public ChannelReader<GraphQLWsEvent> Events => _events.Reader;

    public GraphQLWsDialect Dialect { get; private set; } = GraphQLWsDialect.TransportWs;

    /// <summary>Connects, negotiates the dialect, sends <c>connection_init</c> (with
    /// <paramref name="connectionInitPayloadJson"/> as its payload when supplied — the Apollo
    /// convention for auth), and waits for <c>connection_ack</c>.</summary>
    public async Task ConnectAsync(
        Uri wsUri,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        string? connectionInitPayloadJson = null,
        CancellationToken cancellationToken = default)
    {
        await _ws.ConnectAsync(
            wsUri, headers,
            subprotocols: new[] { TransportWsProtocol, LegacyWsProtocol },
            cancellationToken).ConfigureAwait(false);

        Dialect = string.Equals(_ws.NegotiatedSubProtocol, LegacyWsProtocol, StringComparison.OrdinalIgnoreCase)
            ? GraphQLWsDialect.LegacyWs
            : GraphQLWsDialect.TransportWs;

        _pumpTask = Task.Run(() => PumpAsync(cancellationToken), CancellationToken.None);

        var init = new JsonObject { ["type"] = "connection_init" };
        if (TryParseNode(connectionInitPayloadJson) is { } payload) init["payload"] = payload;
        await _ws.SendTextAsync(init.ToJsonString(), cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_ackTimeout);
        try
        {
            await _ack.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The server did not acknowledge the GraphQL WebSocket connection within {_ackTimeout.TotalSeconds:0.#}s.");
        }
    }

    /// <summary>Starts a subscription (or any operation — the protocol doesn't care) and
    /// returns its id for <see cref="StopAsync"/>.</summary>
    public async Task<string> SubscribeAsync(
        string query, string? variablesJson = null, string? operationName = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var payload = new JsonObject { ["query"] = query };
        if (operationName is not null) payload["operationName"] = operationName;
        payload["variables"] = TryParseNode(variablesJson) ?? new JsonObject();

        var msg = new JsonObject
        {
            ["id"] = id,
            ["type"] = Dialect == GraphQLWsDialect.LegacyWs ? "start" : "subscribe",
            ["payload"] = payload,
        };
        await _ws.SendTextAsync(msg.ToJsonString(), cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Tells the server to stop the subscription (client-initiated complete).</summary>
    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        var msg = new JsonObject
        {
            ["id"] = id,
            ["type"] = Dialect == GraphQLWsDialect.LegacyWs ? "stop" : "complete",
        };
        try
        {
            await _ws.SendTextAsync(msg.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Socket already closed — stopping a dead subscription is a no-op.
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        // The WebSocket close handshake waits for the server's ACK — a dead or rude server
        // would hang teardown forever. Cap it; DisposeAsync's executor disposal aborts the
        // socket underneath if the handshake never completes.
        try
        {
            await _ws.CloseAsync("client closing", cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* abort on dispose */ }
        _events.Writer.TryComplete();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _ws.Events.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (frame.Direction == WebSocketMessageDirection.System)
                {
                    Emit(GraphQLWsEventKind.System, null, JsonSerializer.Serialize(frame.Payload), frame.Timestamp);
                    continue;
                }
                if (frame.Direction != WebSocketMessageDirection.Received || frame.IsBinary) continue;
                await HandleFrameAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Emit(GraphQLWsEventKind.System, null, JsonSerializer.Serialize($"Protocol error: {ex.Message}"),
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _ack.TrySetCanceled();
            _events.Writer.TryComplete();
        }
    }

    private async Task HandleFrameAsync(WebSocketMessageRecord frame, CancellationToken ct)
    {
        string? type = null, id = null, payloadJson = "null";
        try
        {
            using var doc = JsonDocument.Parse(frame.Payload);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            payloadJson = root.TryGetProperty("payload", out var p) ? p.GetRawText() : "null";
        }
        catch (JsonException)
        {
            Emit(GraphQLWsEventKind.System, null,
                JsonSerializer.Serialize($"Unparseable frame: {Truncate(frame.Payload)}"), frame.Timestamp);
            return;
        }

        switch (type)
        {
            case "connection_ack":
                _ack.TrySetResult();
                Emit(GraphQLWsEventKind.System, null, "\"Connection acknowledged\"", frame.Timestamp);
                break;
            case "ping": // transport-ws keep-alive: must answer with pong
                try { await _ws.SendTextAsync("{\"type\":\"pong\"}", ct).ConfigureAwait(false); }
                catch { /* socket teardown race */ }
                break;
            case "pong":
            case "ka": // legacy keep-alive
                break;
            case "next": // transport-ws result
            case "data": // legacy result
                Emit(GraphQLWsEventKind.Next, id, payloadJson, frame.Timestamp);
                break;
            case "error":
                Emit(GraphQLWsEventKind.Error, id, payloadJson, frame.Timestamp);
                break;
            case "complete":
                Emit(GraphQLWsEventKind.Complete, id, payloadJson, frame.Timestamp);
                break;
            case "connection_error": // legacy: server refused the init
                _ack.TrySetException(new InvalidOperationException(
                    $"Server refused the GraphQL WebSocket connection: {payloadJson}"));
                Emit(GraphQLWsEventKind.Error, null, payloadJson, frame.Timestamp);
                break;
            default:
                Emit(GraphQLWsEventKind.System, id,
                    JsonSerializer.Serialize($"Unknown frame type \"{type}\""), frame.Timestamp);
                break;
        }
    }

    private void Emit(GraphQLWsEventKind kind, string? id, string payloadJson, DateTimeOffset ts) =>
        _events.Writer.TryWrite(new GraphQLWsEvent(kind, id, payloadJson, ts));

    private static JsonNode? TryParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        // Dispose the executor BEFORE awaiting the pump: when the close handshake timed out
        // (rude server), the executor's event channel only completes once disposal aborts
        // the socket — awaiting the pump first would deadlock.
        await _ws.DisposeAsync().ConfigureAwait(false);
        if (_pumpTask is not null) try { await _pumpTask.ConfigureAwait(false); } catch { /* teardown */ }
    }
}
