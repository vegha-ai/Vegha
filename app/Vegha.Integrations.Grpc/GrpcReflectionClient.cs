using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Vegha.Integrations.Grpc;

/// <summary>
/// Minimal client for the gRPC v1 reflection service. Lists exposed services and
/// fetches their FileDescriptorProtos. The .proto descriptor parsing — turning those
/// raw bytes into the message tree the UI needs — is layered on top of this and
/// composes with <see cref="GrpcUnaryExecutor"/> at the wire level.
///
/// Falls back to v1alpha (which many older servers still expose) when v1 isn't found.
/// </summary>
public sealed class GrpcReflectionClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly CallInvoker _invoker;

    public GrpcReflectionClient(string address)
    {
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        _channel = GrpcChannel.ForAddress(address);
        _invoker = _channel.CreateCallInvoker();
    }

    /// <summary>Returns the list of services advertised by the reflection service. Tries v1
    /// first; falls back to v1alpha for servers that still ship the older surface.</summary>
    public async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var requestBytes = BuildListServicesRequest();
        var response = await CallReflectionAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        return ParseServiceList(response);
    }

    /// <summary>Returns the FileDescriptorProto bytes containing <paramref name="symbol"/> (a
    /// fully-qualified service or message name). The caller decodes them with
    /// <c>FileDescriptor.BuildFromByteStrings</c> + walks the tree.</summary>
    public async Task<IReadOnlyList<byte[]>> GetFileContainingSymbolAsync(
        string symbol, CancellationToken cancellationToken = default)
    {
        var requestBytes = BuildFileContainingSymbolRequest(symbol);
        var response = await CallReflectionAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        return ParseFileDescriptorResponse(response);
    }

    private async Task<byte[]> CallReflectionAsync(byte[] request, CancellationToken ct)
    {
        var marshaller = Marshallers.Create(b => (byte[])b, b => (byte[])b);

        var v1 = new Method<byte[], byte[]>(
            MethodType.DuplexStreaming, "grpc.reflection.v1.ServerReflection",
            "ServerReflectionInfo", marshaller, marshaller);
        try { return await TryWithMethodAsync(v1, request, ct).ConfigureAwait(false); }
        catch (RpcException) { /* fall through to v1alpha */ }

        var v1alpha = new Method<byte[], byte[]>(
            MethodType.DuplexStreaming, "grpc.reflection.v1alpha.ServerReflection",
            "ServerReflectionInfo", marshaller, marshaller);
        return await TryWithMethodAsync(v1alpha, request, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> TryWithMethodAsync(Method<byte[], byte[]> method, byte[] request, CancellationToken ct)
    {
        var options = new CallOptions(cancellationToken: ct);
        using var call = _invoker.AsyncDuplexStreamingCall(method, host: null, options);
        await call.RequestStream.WriteAsync(request, ct).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        if (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            return call.ResponseStream.Current;
        return Array.Empty<byte>();
    }

    // ---- Hand-rolled protobuf encoding ----
    // ServerReflectionRequest is small enough that hand-encoding beats pulling in the
    // generated reflection.proto bindings; we own only the two request shapes we need.

    private static byte[] BuildListServicesRequest()
    {
        // ServerReflectionRequest { string list_services = 3; }
        var stream = new MemoryStream();
        WriteTag(stream, fieldNumber: 3, wireType: 2);
        WriteVarint(stream, 0);
        return stream.ToArray();
    }

    private static byte[] BuildFileContainingSymbolRequest(string symbol)
    {
        // ServerReflectionRequest { string file_containing_symbol = 4; }
        var bytes = System.Text.Encoding.UTF8.GetBytes(symbol);
        var stream = new MemoryStream();
        WriteTag(stream, fieldNumber: 4, wireType: 2);
        WriteVarint(stream, bytes.Length);
        stream.Write(bytes);
        return stream.ToArray();
    }

    private static IReadOnlyList<string> ParseServiceList(byte[] responseBytes)
    {
        // ServerReflectionResponse { ListServiceResponse list_services_response = 6; }
        // ListServiceResponse { repeated ServiceResponse service = 1; }
        // ServiceResponse { string name = 1; }
        if (responseBytes.Length == 0) return Array.Empty<string>();
        var input = new CodedInputStream(responseBytes);
        var services = new List<string>();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == 6)
                ParseListServiceResponse(input.ReadBytes().ToByteArray(), services);
            else input.SkipLastField();
        }
        return services;
    }

    private static void ParseListServiceResponse(byte[] bytes, List<string> services)
    {
        var input = new CodedInputStream(bytes);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == 1)
                ParseServiceResponse(input.ReadBytes().ToByteArray(), services);
            else input.SkipLastField();
        }
    }

    private static void ParseServiceResponse(byte[] bytes, List<string> services)
    {
        var input = new CodedInputStream(bytes);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == 1) services.Add(input.ReadString());
            else input.SkipLastField();
        }
    }

    private static IReadOnlyList<byte[]> ParseFileDescriptorResponse(byte[] responseBytes)
    {
        // ServerReflectionResponse { FileDescriptorResponse file_descriptor_response = 4; }
        // FileDescriptorResponse { repeated bytes file_descriptor_proto = 1; }
        if (responseBytes.Length == 0) return Array.Empty<byte[]>();
        var input = new CodedInputStream(responseBytes);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == 4)
                return ParseFileDescriptorResponseInner(input.ReadBytes().ToByteArray());
            input.SkipLastField();
        }
        return Array.Empty<byte[]>();
    }

    private static IReadOnlyList<byte[]> ParseFileDescriptorResponseInner(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        var protos = new List<byte[]>();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == 1) protos.Add(input.ReadBytes().ToByteArray());
            else input.SkipLastField();
        }
        return protos;
    }

    private static void WriteTag(Stream s, int fieldNumber, int wireType)
    {
        var tag = (uint)((fieldNumber << 3) | wireType);
        WriteVarint(s, tag);
    }

    private static void WriteVarint(Stream s, long value)
    {
        var v = (ulong)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
