using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Vegha.Integrations.Grpc;

/// <summary>
/// Result of a unary call. Status carries the gRPC status code + textual detail; for OK
/// calls Bytes carries the serialized protobuf response. The caller decodes Bytes once
/// it has the message descriptor (descriptor parsing is in v1.A2 alongside reflection).
/// </summary>
public sealed record GrpcUnaryResult(
    StatusCode StatusCode,
    string StatusDetail,
    byte[]? Bytes,
    long ElapsedMs,
    IReadOnlyList<KeyValuePair<string, string>> TrailingMetadata,
    string? ErrorMessage);

/// <summary>Inputs to a unary call.</summary>
public sealed record GrpcUnaryRequest(
    string Address,
    string FullyQualifiedMethod,
    byte[] RequestPayload,
    IReadOnlyList<KeyValuePair<string, string>>? Metadata = null,
    TimeSpan? Deadline = null);

/// <summary>
/// Performs a unary gRPC call against an arbitrary service+method using the bytes-only
/// marshaller. Schema-aware encoding (JSON ↔ proto) lives in the descriptor layer and
/// composes with this executor — this class only deals with the wire transport.
///
/// Method format: <c>/package.Service/Method</c> (the canonical gRPC URI form). Address
/// is the server URL including scheme (https for TLS, http for plaintext development
/// with <c>EnableUnencryptedHttp2</c>).
/// </summary>
public sealed class GrpcUnaryExecutor
{
    /// <summary>Identity marshaller — bytes in, bytes out. The host serializes/deserializes
    /// at a higher layer once the .proto descriptor is known.</summary>
    private static readonly Marshaller<byte[]> ByteMarshaller =
        Marshallers.Create(b => b, b => b);

    public async Task<GrpcUnaryResult> ExecuteAsync(GrpcUnaryRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // For plaintext (http://) servers we need to opt into unencrypted HTTP/2 globally.
        if (request.Address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        using var channel = GrpcChannel.ForAddress(request.Address);
        var invoker = channel.CreateCallInvoker();

        var slash = request.FullyQualifiedMethod.LastIndexOf('/');
        if (slash <= 0 || slash >= request.FullyQualifiedMethod.Length - 1)
            return new GrpcUnaryResult(StatusCode.Unknown, "method must be /package.Service/Method",
                null, sw.ElapsedMilliseconds, Array.Empty<KeyValuePair<string, string>>(),
                "Invalid method format");

        var serviceName = request.FullyQualifiedMethod[1..slash]; // strip leading '/'
        var methodName = request.FullyQualifiedMethod[(slash + 1)..];

        var method = new Method<byte[], byte[]>(
            MethodType.Unary, serviceName, methodName, ByteMarshaller, ByteMarshaller);

        Metadata? metadata = null;
        if (request.Metadata is not null && request.Metadata.Count > 0)
        {
            metadata = new Metadata();
            foreach (var (k, v) in request.Metadata) metadata.Add(k, v);
        }

        var options = new CallOptions(
            headers: metadata,
            deadline: request.Deadline.HasValue ? DateTime.UtcNow + request.Deadline.Value : null,
            cancellationToken: cancellationToken);

        try
        {
            using var call = invoker.AsyncUnaryCall(method, host: null, options, request.RequestPayload);
            var response = await call.ResponseAsync.ConfigureAwait(false);
            var trailers = (await call.ResponseHeadersAsync.ConfigureAwait(false))
                .Select(m => new KeyValuePair<string, string>(m.Key, m.Value))
                .ToList();
            sw.Stop();
            return new GrpcUnaryResult(StatusCode.OK, "OK", response, sw.ElapsedMilliseconds, trailers, null);
        }
        catch (RpcException rpc)
        {
            sw.Stop();
            var trailers = rpc.Trailers
                .Select(m => new KeyValuePair<string, string>(m.Key, m.Value))
                .ToList();
            return new GrpcUnaryResult(rpc.Status.StatusCode, rpc.Status.Detail, null,
                sw.ElapsedMilliseconds, trailers, rpc.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new GrpcUnaryResult(StatusCode.Unknown, "transport error", null,
                sw.ElapsedMilliseconds, Array.Empty<KeyValuePair<string, string>>(), ex.Message);
        }
    }
}
