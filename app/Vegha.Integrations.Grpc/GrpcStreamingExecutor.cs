using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;

namespace Vegha.Integrations.Grpc;

/// <summary>
/// Direction of a streamed message — sent by the client or received from the server.
/// Matches WebSocket's record type one-for-one so UI components can be reused.
/// </summary>
public enum GrpcMessageDirection { Sent, Received, System }

public sealed record GrpcStreamMessage(
    GrpcMessageDirection Direction,
    byte[] Payload,
    DateTimeOffset Timestamp,
    string? Note = null);

/// <summary>
/// Streaming variant of <see cref="GrpcUnaryExecutor"/>. Supports server-streaming,
/// client-streaming, and bidirectional calls. Caller writes outgoing messages via
/// <see cref="SendAsync"/>; the channel reader (<see cref="Events"/>) yields every
/// message in chronological order for the UI to render.
/// </summary>
public sealed class GrpcStreamingExecutor : IAsyncDisposable
{
    private static readonly Marshaller<byte[]> ByteMarshaller =
        Marshallers.Create(b => b, b => b);

    private readonly Channel<GrpcStreamMessage> _events = Channel.CreateUnbounded<GrpcStreamMessage>();
    private GrpcChannel? _channel;
    private object? _call; // AsyncServerStreamingCall, AsyncClientStreamingCall, or AsyncDuplexStreamingCall
    private IClientStreamWriter<byte[]>? _requestStream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public ChannelReader<GrpcStreamMessage> Events => _events.Reader;

    public Task StartServerStreamingAsync(
        GrpcUnaryRequest request, CancellationToken cancellationToken = default)
    {
        var (channel, invoker, name, options) = SetupChannel(request, cancellationToken);
        var method = new Method<byte[], byte[]>(
            MethodType.ServerStreaming, name.service, name.method, ByteMarshaller, ByteMarshaller);
        _channel = channel;
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var call = invoker.AsyncServerStreamingCall(method, host: null, options, request.RequestPayload);
        _call = call;
        _readerTask = Task.Run(() => DrainServerStreamAsync(call, _readerCts.Token));
        return Task.CompletedTask;
    }

    public Task StartClientStreamingAsync(
        string address, string fullyQualifiedMethod,
        IReadOnlyList<KeyValuePair<string, string>>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var (channel, invoker, name, options) = SetupChannel(
            new GrpcUnaryRequest(address, fullyQualifiedMethod, Array.Empty<byte>(), metadata),
            cancellationToken);
        var method = new Method<byte[], byte[]>(
            MethodType.ClientStreaming, name.service, name.method, ByteMarshaller, ByteMarshaller);
        _channel = channel;
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var call = invoker.AsyncClientStreamingCall(method, host: null, options);
        _call = call;
        _requestStream = call.RequestStream;
        _readerTask = Task.Run(() => DrainClientStreamAsync(call, _readerCts.Token));
        return Task.CompletedTask;
    }

    public Task StartDuplexStreamingAsync(
        string address, string fullyQualifiedMethod,
        IReadOnlyList<KeyValuePair<string, string>>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var (channel, invoker, name, options) = SetupChannel(
            new GrpcUnaryRequest(address, fullyQualifiedMethod, Array.Empty<byte>(), metadata),
            cancellationToken);
        var method = new Method<byte[], byte[]>(
            MethodType.DuplexStreaming, name.service, name.method, ByteMarshaller, ByteMarshaller);
        _channel = channel;
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var call = invoker.AsyncDuplexStreamingCall(method, host: null, options);
        _call = call;
        _requestStream = call.RequestStream;
        _readerTask = Task.Run(() => DrainDuplexAsync(call, _readerCts.Token));
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (_requestStream is null)
            throw new InvalidOperationException("No outgoing request stream — server-streaming calls don't accept input.");
        await _requestStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _events.Writer.WriteAsync(
            new GrpcStreamMessage(GrpcMessageDirection.Sent, payload, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSendsAsync()
    {
        if (_requestStream is null) return;
        await _requestStream.CompleteAsync().ConfigureAwait(false);
    }

    private async Task DrainServerStreamAsync(AsyncServerStreamingCall<byte[]> call, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _events.Writer.WriteAsync(
                    new GrpcStreamMessage(GrpcMessageDirection.Received, msg, DateTimeOffset.UtcNow), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) { await ReportError(ex); }
        finally { _events.Writer.TryComplete(); }
    }

    private async Task DrainClientStreamAsync(AsyncClientStreamingCall<byte[], byte[]> call, CancellationToken ct)
    {
        try
        {
            var resp = await call.ResponseAsync.ConfigureAwait(false);
            await _events.Writer.WriteAsync(
                new GrpcStreamMessage(GrpcMessageDirection.Received, resp, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) { await ReportError(ex); }
        finally { _events.Writer.TryComplete(); }
    }

    private async Task DrainDuplexAsync(AsyncDuplexStreamingCall<byte[], byte[]> call, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _events.Writer.WriteAsync(
                    new GrpcStreamMessage(GrpcMessageDirection.Received, msg, DateTimeOffset.UtcNow), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) { await ReportError(ex); }
        finally { _events.Writer.TryComplete(); }
    }

    private async Task ReportError(Exception ex)
    {
        try
        {
            await _events.Writer.WriteAsync(new GrpcStreamMessage(
                GrpcMessageDirection.System,
                System.Text.Encoding.UTF8.GetBytes(ex.Message),
                DateTimeOffset.UtcNow,
                Note: "error"), CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* swallow — channel may already be completed */ }
    }

    private static (GrpcChannel channel, CallInvoker invoker, (string service, string method) name, CallOptions options)
        SetupChannel(GrpcUnaryRequest request, CancellationToken ct)
    {
        if (request.Address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var channel = GrpcChannel.ForAddress(request.Address);
        var invoker = channel.CreateCallInvoker();

        var slash = request.FullyQualifiedMethod.LastIndexOf('/');
        if (slash <= 0) throw new ArgumentException("Method must be /package.Service/Method");

        var service = request.FullyQualifiedMethod[1..slash];
        var method = request.FullyQualifiedMethod[(slash + 1)..];

        Metadata? metadata = null;
        if (request.Metadata is not null && request.Metadata.Count > 0)
        {
            metadata = new Metadata();
            foreach (var (k, v) in request.Metadata) metadata.Add(k, v);
        }

        var options = new CallOptions(
            headers: metadata,
            deadline: request.Deadline.HasValue ? DateTime.UtcNow + request.Deadline.Value : null,
            cancellationToken: ct);

        return (channel, invoker, (service, method), options);
    }

    public async ValueTask DisposeAsync()
    {
        try { _readerCts?.Cancel(); } catch { }
        if (_call is IDisposable d) try { d.Dispose(); } catch { }
        if (_readerTask is not null) try { await _readerTask.ConfigureAwait(false); } catch { }
        _channel?.Dispose();
        _readerCts?.Dispose();
    }
}
