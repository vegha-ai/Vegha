using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// Drives <see cref="GraphQLWsClient"/> against a scripted in-process WebSocket server
/// (same HttpListener harness as <see cref="WebSocketExecutorTests"/>). Each test supplies
/// the server side of the conversation.
/// </summary>
public class GraphQLWsClientTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private CancellationTokenSource _serverCts = null!;
    private Uri _wsUrl = null!;
    private Func<WebSocket, CancellationToken, Task>? _serverScript;
    private string? _acceptSubProtocol = GraphQLWsClient.TransportWsProtocol;

    public Task InitializeAsync()
    {
        var port = FindFreePort();
        _wsUrl = new Uri($"ws://127.0.0.1:{port}/graphql");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/graphql/");
        _listener.Start();
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => RunServer(_serverCts.Token));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _serverCts.Cancel(); } catch { }
        try { _listener.Close(); } catch { }
        return Task.CompletedTask;
    }

    private async Task RunServer(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync();
                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                var wsCtx = await ctx.AcceptWebSocketAsync(_acceptSubProtocol);
                _ = Task.Run(() => _serverScript!(wsCtx.WebSocket, ct));
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- Server-side helpers ----

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var r = await ws.ReceiveAsync(buf, ct);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
    }

    private static Task SendAsync(WebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    /// <summary>Keeps the server side reading until the client's Close frame arrives, then
    /// completes the close handshake — without this, client disposal would wait 3 s per test
    /// for a close ACK that never comes.</summary>
    private static async Task DrainUntilCloseAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }
            }
        }
        catch { /* client aborted — fine */ }
    }

    private static async Task<List<GraphQLWsEvent>> DrainAsync(
        GraphQLWsClient client, Func<List<GraphQLWsEvent>, bool> until, int timeoutSeconds = 5)
    {
        var events = new List<GraphQLWsEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await foreach (var e in client.Events.ReadAllAsync(cts.Token))
            {
                events.Add(e);
                if (until(events)) break;
            }
        }
        catch (OperationCanceledException) { /* timeout — assertions below explain */ }
        return events;
    }

    // ---- Tests ----

    [Fact]
    public async Task TransportWs_HappyPath_InitAckSubscribeNextComplete()
    {
        _serverScript = async (ws, ct) =>
        {
            using var init = await ReceiveJsonAsync(ws, ct);
            init.RootElement.GetProperty("type").GetString().Should().Be("connection_init");
            await SendAsync(ws, """{"type":"connection_ack"}""", ct);

            using var sub = await ReceiveJsonAsync(ws, ct);
            sub.RootElement.GetProperty("type").GetString().Should().Be("subscribe");
            var id = sub.RootElement.GetProperty("id").GetString();
            sub.RootElement.GetProperty("payload").GetProperty("query").GetString()
                .Should().Contain("userChanged");

            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"next\",\"payload\":{\"data\":{\"userChanged\":{\"id\":\"u1\"}}}}", ct);
            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"next\",\"payload\":{\"data\":{\"userChanged\":{\"id\":\"u2\"}}}}", ct);
            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"complete\"}", ct);
            await DrainUntilCloseAsync(ws, ct);
        };

        await using var client = new GraphQLWsClient();
        await client.ConnectAsync(_wsUrl);
        client.Dialect.Should().Be(GraphQLWsDialect.TransportWs);

        var id = await client.SubscribeAsync("subscription S { userChanged { id } }");
        var events = await DrainAsync(client, evs => evs.Any(e => e.Kind == GraphQLWsEventKind.Complete));

        var next = events.Where(e => e.Kind == GraphQLWsEventKind.Next).ToList();
        next.Should().HaveCount(2);
        next[0].SubscriptionId.Should().Be(id);
        next[0].PayloadJson.Should().Contain("\"u1\"");
        next[1].PayloadJson.Should().Contain("\"u2\"");
        events.Should().Contain(e => e.Kind == GraphQLWsEventKind.Complete);
    }

    [Fact]
    public async Task TransportWs_ServerError_SurfacesErrorEvent()
    {
        _serverScript = async (ws, ct) =>
        {
            using var _ = await ReceiveJsonAsync(ws, ct);
            await SendAsync(ws, """{"type":"connection_ack"}""", ct);
            using var sub = await ReceiveJsonAsync(ws, ct);
            var id = sub.RootElement.GetProperty("id").GetString();
            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"error\",\"payload\":[{\"message\":\"boom\"}]}", ct);
            await DrainUntilCloseAsync(ws, ct);
        };

        await using var client = new GraphQLWsClient();
        await client.ConnectAsync(_wsUrl);
        await client.SubscribeAsync("subscription S { x }");
        var events = await DrainAsync(client, evs => evs.Any(e => e.Kind == GraphQLWsEventKind.Error));

        events.Should().Contain(e => e.Kind == GraphQLWsEventKind.Error && e.PayloadJson.Contains("boom"));
    }

    [Fact]
    public async Task TransportWs_Ping_IsAnsweredWithPong()
    {
        var pongReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverScript = async (ws, ct) =>
        {
            using var _ = await ReceiveJsonAsync(ws, ct);
            await SendAsync(ws, """{"type":"connection_ack"}""", ct);
            await SendAsync(ws, """{"type":"ping"}""", ct);
            using var reply = await ReceiveJsonAsync(ws, ct);
            if (reply.RootElement.GetProperty("type").GetString() == "pong")
                pongReceived.TrySetResult();
            await DrainUntilCloseAsync(ws, ct);
        };

        await using var client = new GraphQLWsClient();
        await client.ConnectAsync(_wsUrl);

        await pongReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LegacyProtocol_Negotiated_UsesStartDataStop()
    {
        _acceptSubProtocol = GraphQLWsClient.LegacyWsProtocol;
        var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverScript = async (ws, ct) =>
        {
            using var _ = await ReceiveJsonAsync(ws, ct);
            await SendAsync(ws, """{"type":"connection_ack"}""", ct);
            await SendAsync(ws, """{"type":"ka"}""", ct); // legacy keep-alive must be tolerated

            using var start = await ReceiveJsonAsync(ws, ct);
            start.RootElement.GetProperty("type").GetString().Should().Be("start");
            var id = start.RootElement.GetProperty("id").GetString();
            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"data\",\"payload\":{\"data\":{\"tick\":1}}}", ct);

            using var stop = await ReceiveJsonAsync(ws, ct);
            if (stop.RootElement.GetProperty("type").GetString() == "stop")
                stopReceived.TrySetResult();
            await DrainUntilCloseAsync(ws, ct);
        };

        await using var client = new GraphQLWsClient();
        await client.ConnectAsync(_wsUrl);
        client.Dialect.Should().Be(GraphQLWsDialect.LegacyWs);

        var id = await client.SubscribeAsync("subscription S { tick }");
        var events = await DrainAsync(client, evs => evs.Any(e => e.Kind == GraphQLWsEventKind.Next));
        events.Should().Contain(e => e.Kind == GraphQLWsEventKind.Next && e.PayloadJson.Contains("tick"));

        await client.StopAsync(id);
        await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AckTimeout_Throws_WhenServerNeverAcks()
    {
        _serverScript = async (ws, ct) =>
        {
            using var _ = await ReceiveJsonAsync(ws, ct); // swallow init, never ack
            await Task.Delay(Timeout.Infinite, ct);
        };

        await using var client = new GraphQLWsClient(ackTimeout: TimeSpan.FromMilliseconds(400));
        var act = async () => await client.ConnectAsync(_wsUrl);
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task MidStreamDrop_CompletesEventStream()
    {
        _serverScript = async (ws, ct) =>
        {
            using var _ = await ReceiveJsonAsync(ws, ct);
            await SendAsync(ws, """{"type":"connection_ack"}""", ct);
            using var sub = await ReceiveJsonAsync(ws, ct);
            var id = sub.RootElement.GetProperty("id").GetString();
            await SendAsync(ws, "{\"id\":\"" + id + "\",\"type\":\"next\",\"payload\":{\"data\":{\"x\":1}}}", ct);
            ws.Abort(); // hard drop, no close handshake
        };

        await using var client = new GraphQLWsClient();
        await client.ConnectAsync(_wsUrl);
        await client.SubscribeAsync("subscription S { x }");

        // The stream must terminate (channel completes) rather than hang after the drop.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sawNext = false;
        await foreach (var e in client.Events.ReadAllAsync(cts.Token))
        {
            if (e.Kind == GraphQLWsEventKind.Next) sawNext = true;
        }
        sawNext.Should().BeTrue();
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
