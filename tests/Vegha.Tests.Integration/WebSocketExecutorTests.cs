using System.Net;
using System.Net.WebSockets;
using System.Text;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// Spins up a tiny in-process WebSocket server (HttpListener) so we can drive the
/// executor end-to-end without external dependencies.
/// </summary>
public class WebSocketExecutorTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private CancellationTokenSource _serverCts = null!;
    private Uri _wsUrl = null!;

    public Task InitializeAsync()
    {
        var port = FindFreePort();
        _wsUrl = new Uri($"ws://127.0.0.1:{port}/ws");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/ws/");
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
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                var ws = wsCtx.WebSocket;
                _ = Task.Run(async () =>
                {
                    var buf = new byte[4096];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                            break;
                        }
                        // Echo back with "echo:" prefix so we can distinguish.
                        var echo = "echo:" + Encoding.UTF8.GetString(buf, 0, r.Count);
                        await ws.SendAsync(Encoding.UTF8.GetBytes(echo), WebSocketMessageType.Text, true, ct);
                    }
                });
            }
        }
        catch (HttpListenerException) { /* listener disposed */ }
        catch (ObjectDisposedException) { /* listener disposed */ }
    }

    [Fact]
    public async Task Connect_SendText_ReceivesEcho_Disconnect()
    {
        await using var executor = new WebSocketExecutor();
        await executor.ConnectAsync(_wsUrl);
        executor.State.Should().Be(WebSocketState.Open);

        await executor.SendTextAsync("hello");

        // Read until we see the echoed reply.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        WebSocketMessageRecord? echo = null;
        await foreach (var msg in executor.Events.ReadAllAsync(cts.Token))
        {
            if (msg.Direction == WebSocketMessageDirection.Received)
            {
                echo = msg;
                break;
            }
        }
        echo.Should().NotBeNull();
        echo!.Payload.Should().Be("echo:hello");

        await executor.CloseAsync();
    }

    [Fact]
    public async Task SendBinary_DeliversBytes_AndStreamLogsBinaryRecord()
    {
        await using var executor = new WebSocketExecutor();
        await executor.ConnectAsync(_wsUrl);
        await executor.SendBinaryAsync(new byte[] { 1, 2, 3, 4, 5 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        WebSocketMessageRecord? sent = null;
        await foreach (var msg in executor.Events.ReadAllAsync(cts.Token))
        {
            if (msg.Direction == WebSocketMessageDirection.Sent)
            {
                sent = msg;
                break;
            }
        }
        sent.Should().NotBeNull();
        sent!.IsBinary.Should().BeTrue();
        sent.Payload.Should().Contain("5 bytes");
    }

    [Fact]
    public async Task Send_WhenSocketNotOpen_Throws()
    {
        await using var executor = new WebSocketExecutor();
        var act = async () => await executor.SendTextAsync("nope");
        await act.Should().ThrowAsync<InvalidOperationException>();
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
