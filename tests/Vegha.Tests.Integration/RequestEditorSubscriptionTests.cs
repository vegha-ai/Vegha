using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// End-to-end: the editor VM's Send routes a subscription operation over graphql-ws to an
/// in-process scripted server, streams frames into <see cref="RequestEditorViewModel.SubscriptionFrames"/>,
/// and Send-again stops the session.
/// </summary>
public class RequestEditorSubscriptionTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private CancellationTokenSource _serverCts = null!;
    private int _port;

    public Task InitializeAsync()
    {
        _port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/graphql/");
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
                var wsCtx = await ctx.AcceptWebSocketAsync(GraphQLWsClient.TransportWsProtocol);
                _ = Task.Run(() => ServerScript(wsCtx.WebSocket, ct));
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ServerScript(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];

        async Task<JsonDocument> Receive()
        {
            var r = await ws.ReceiveAsync(buf, ct);
            return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
        }
        Task Send(string json) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

        using (var init = await Receive())
            init.RootElement.GetProperty("type").GetString().Should().Be("connection_init");
        await Send("{\"type\":\"connection_ack\"}");

        string? id;
        using (var sub = await Receive())
        {
            sub.RootElement.GetProperty("type").GetString().Should().Be("subscribe");
            id = sub.RootElement.GetProperty("id").GetString();
        }
        await Send("{\"id\":\"" + id + "\",\"type\":\"next\",\"payload\":{\"data\":{\"tick\":1}}}");
        await Send("{\"id\":\"" + id + "\",\"type\":\"next\",\"payload\":{\"data\":{\"tick\":2}}}");

        // Wait for the client's stop ("complete"), then finish the close handshake.
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
        catch { /* client aborted */ }
    }

    private static RequestEditorViewModel CreateVm()
    {
        var http = new HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    [Fact]
    public async Task Send_SubscriptionOperation_StreamsFrames_SendAgainStops()
    {
        var vm = CreateVm();
        vm.Method = "POST";
        vm.Url = $"http://127.0.0.1:{_port}/graphql"; // ws:// is derived from this
        vm.BodyType = "graphql";
        vm.GraphQLQuery = "subscription OnTick { tick }";

        await vm.SendCommand.ExecuteAsync(null);
        vm.IsSubscriptionActive.Should().BeTrue("Send on a subscription operation must open a graphql-ws session");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.SubscriptionFrames.Count(f => f.Kind == "data") < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        vm.SubscriptionFrames.Count(f => f.Kind == "data").Should().Be(2);
        vm.SubscriptionFrames.Where(f => f.Kind == "data").Last().PayloadJson.Should().Contain("\"tick\":2");
        vm.HasResponse.Should().BeTrue();
        vm.ResponseBody.Should().Contain("tick");

        // Second Send = Stop. The pump's teardown flips IsSubscriptionActive off.
        await vm.SendCommand.ExecuteAsync(null);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsSubscriptionActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        vm.IsSubscriptionActive.Should().BeFalse();
        vm.ResponseStatusText.Should().Contain("Subscription ended");
    }

    [Theory]
    [InlineData("http://api.acme.io/graphql", null, "ws://api.acme.io/graphql")]
    [InlineData("https://api.acme.io/graphql", null, "wss://api.acme.io/graphql")]
    [InlineData("https://api.acme.io/graphql", "wss://stream.acme.io/ws", "wss://stream.acme.io/ws")]
    [InlineData("wss://already.ws/x", null, "wss://already.ws/x")]
    public void DeriveWsUri_SwapsScheme_OrHonorsOverride(string httpUrl, string? overrideUrl, string expected)
    {
        RequestEditorViewModel.DeriveWsUri(httpUrl, overrideUrl).ToString()
            .Should().StartWith(expected);
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
