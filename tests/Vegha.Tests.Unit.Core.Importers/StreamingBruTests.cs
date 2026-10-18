using FluentAssertions;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>WebSocket and gRPC requests persist as <c>ws { }</c> / <c>grpc { }</c> verb blocks
/// and survive a parse → emit → parse round trip.</summary>
public class StreamingBruTests
{
    private static RequestItem Parse(string text)
    {
        BruParser.TryParse(text, out var doc, out var error).Should().BeTrue(error);
        return BruToRequestConverter.Convert(doc!);
    }

    [Fact]
    public void Grpc_request_round_trips()
    {
        var original = new RequestItem
        {
            Name = "Say hello",
            Kind = RequestKind.Grpc,
            Method = "GRPC",
            Url = "{{grpcHost}}:50051",
            Grpc = new GrpcConfig { Method = "/helloworld.Greeter/SayHello", MethodType = "unary" },
            Headers = new List<KvPair> { new("authorization", "Bearer {{token}}") },
            Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\n  \"name\": \"Ada\"\n}" },
        };

        var text = BruEmitter.Emit(original);
        text.Should().Contain("grpc {").And.Contain("method: /helloworld.Greeter/SayHello");

        var back = Parse(text);
        back.Kind.Should().Be(RequestKind.Grpc);
        back.Url.Should().Be(original.Url);
        back.Grpc.Should().BeEquivalentTo(original.Grpc);
        back.Body.Content.Should().Contain("\"Ada\"");
        back.Headers.Should().ContainSingle();
        BruEmitter.Emit(back).Should().Be(text);
    }

    [Fact]
    public void WebSocket_request_round_trips()
    {
        var original = new RequestItem
        {
            Name = "Live",
            Kind = RequestKind.WebSocket,
            Method = "WS",
            Url = "wss://{{host}}/live",
            Body = new BodyConfig { Mode = BodyMode.Text, Content = "ping" },
        };

        var text = BruEmitter.Emit(original);
        text.Should().Contain("ws {");
        var back = Parse(text);
        back.Kind.Should().Be(RequestKind.WebSocket);
        back.Method.Should().Be("WS");
        back.Url.Should().Be(original.Url);
        back.Body.Content!.Trim().Should().Be("ping");
        BruEmitter.Emit(back).Should().Be(text);
    }

    [Theory]
    [InlineData("ws", RequestKind.WebSocket)]
    [InlineData("grpc", RequestKind.Grpc)]
    public void Desktop_scaffolds_with_only_meta_type_load_as_the_streaming_kind(string type, RequestKind kind)
    {
        var r = Parse($"meta {{\n  name: x\n  type: {type}\n  seq: 1\n}}\n\nget {{\n  url: https://example.com/\n}}\n");
        r.Kind.Should().Be(kind);
    }
}
