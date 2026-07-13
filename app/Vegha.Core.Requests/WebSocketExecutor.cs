using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Vegha.Core.Requests;

/// <summary>
/// Direction of a captured message — sent by the client or received from the server.
/// </summary>
public enum WebSocketMessageDirection
{
    Sent,
    Received,
    System,
}

/// <summary>One frame on the wire (sent or received) plus the timestamp it crossed our boundary.</summary>
public sealed record WebSocketMessageRecord(
    WebSocketMessageDirection Direction,
    string Payload,
    DateTimeOffset Timestamp,
    bool IsBinary,
    string? Note = null);

/// <summary>
/// Wraps <see cref="ClientWebSocket"/> with a simple connect → send/receive loop and a
/// channel-backed event stream the UI can consume. One executor per connection; create
/// a fresh one per session.
/// </summary>
public sealed class WebSocketExecutor : IAsyncDisposable
{
    private readonly ClientWebSocket _client = new();
    private readonly Channel<WebSocketMessageRecord> _events = Channel.CreateUnbounded<WebSocketMessageRecord>();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;

    /// <summary>Stream of frames (sent + received + system events). Drains until the connection closes.</summary>
    public ChannelReader<WebSocketMessageRecord> Events => _events.Reader;

    public WebSocketState State => _client.State;

    /// <summary>Subprotocol the server accepted during the handshake (null when none was
    /// negotiated). Lets protocol layers (graphql-ws) pick their dialect.</summary>
    public string? NegotiatedSubProtocol => _client.SubProtocol;

    /// <summary>Connect to the URL. Adds the supplied subprotocols + headers (e.g., Authorization)
    /// before opening. Throws on failure — the caller decides whether to surface to the user.</summary>
    public async Task ConnectAsync(
        Uri url,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        IEnumerable<string>? subprotocols = null,
        CancellationToken cancellationToken = default)
    {
        if (subprotocols is not null)
            foreach (var sp in subprotocols) _client.Options.AddSubProtocol(sp);

        if (headers is not null)
            foreach (var (k, v) in headers) _client.Options.SetRequestHeader(k, v);

        await _client.ConnectAsync(url, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(new WebSocketMessageRecord(
            WebSocketMessageDirection.System, $"Connected to {url}", DateTimeOffset.UtcNow, IsBinary: false),
            cancellationToken).ConfigureAwait(false);

        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token));
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_client.State != WebSocketState.Open)
            throw new InvalidOperationException($"Cannot send: socket is {_client.State}");

        var bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        await _client.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(new WebSocketMessageRecord(
            WebSocketMessageDirection.Sent, message ?? string.Empty, DateTimeOffset.UtcNow, IsBinary: false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_client.State != WebSocketState.Open)
            throw new InvalidOperationException($"Cannot send: socket is {_client.State}");

        await _client.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(new WebSocketMessageRecord(
            WebSocketMessageDirection.Sent, $"<binary {data.Length} bytes>", DateTimeOffset.UtcNow, IsBinary: true),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (_client.State == WebSocketState.Open)
        {
            try
            {
                await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, reason ?? "client closing",
                    cancellationToken).ConfigureAwait(false);
            }
            catch { /* best-effort close */ }
        }
        _readerCts.Cancel();
        _events.Writer.TryComplete();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var assembly = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _client.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                assembly.SetLength(0);
                do
                {
                    result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    assembly.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage && !ct.IsCancellationRequested);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _events.Writer.WriteAsync(new WebSocketMessageRecord(
                        WebSocketMessageDirection.System,
                        $"Server closed: {result.CloseStatus} {result.CloseStatusDescription}",
                        DateTimeOffset.UtcNow, IsBinary: false), CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                var bytes = assembly.ToArray();
                var isBinary = result.MessageType == WebSocketMessageType.Binary;
                var payload = isBinary
                    ? $"<binary {bytes.Length} bytes>"
                    : Encoding.UTF8.GetString(bytes);

                await _events.Writer.WriteAsync(new WebSocketMessageRecord(
                    WebSocketMessageDirection.Received, payload, DateTimeOffset.UtcNow, isBinary),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on disposal */ }
        catch (Exception ex)
        {
            await _events.Writer.WriteAsync(new WebSocketMessageRecord(
                WebSocketMessageDirection.System, $"Read error: {ex.Message}",
                DateTimeOffset.UtcNow, IsBinary: false), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (_readerTask is not null) try { await _readerTask.ConfigureAwait(false); } catch { /* expected */ }
        _readerCts.Dispose();
        _client.Dispose();
    }
}
