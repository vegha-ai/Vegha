using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>WebSocket / gRPC requests: resolved like any send (env, inherited headers, auth),
/// but never pushed through the one-shot HTTP pipeline.</summary>
public class StreamingRequestTests
{
    private static readonly Collection Coll = new()
    {
        Name = "c",
        Headers = new List<KvPair> { new("X-Collection", "{{tenant}}") },
        Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "{{token}}" } },
    };

    private static RequestPipeline.Inputs Inputs(RequestItem request) => new(
        Coll, Array.Empty<Folder>(), request,
        new Dictionary<string, string> { ["host"] = "api.test", ["tenant"] = "acme", ["token"] = "t0k" },
        new Dictionary<string, string>());

    [Fact]
    public async Task Prepare_resolves_url_headers_auth_and_first_message()
    {
        var ws = new RequestItem
        {
            Kind = RequestKind.WebSocket,
            Url = "https://{{host}}/live",
            Params = new List<KvPair> { new("room", "{{tenant}}") },
            Headers = new List<KvPair> { new("X-Req", "1") },
            Auth = new AuthConfig { Type = AuthType.Inherit },
            Body = new BodyConfig { Mode = BodyMode.Json, Content = """{"hello":"{{tenant}}"}""" },
        };

        var p = await RequestPipeline.PrepareAsync(Inputs(ws));

        p.Error.Should().BeNull();
        p.Url!.ToString().Should().Be("wss://api.test/live?room=acme");
        p.Headers.Should().Contain(new KeyValuePair<string, string>("X-Collection", "acme"));
        p.Headers.Should().Contain(new KeyValuePair<string, string>("X-Req", "1"));
        p.Headers.Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer t0k");
        p.Body.Should().Be("""{"hello":"acme"}""");
        p.UnresolvedVariables.Should().BeEmpty();
    }

    [Theory]
    [InlineData("localhost:50051", "http://localhost:50051")]
    [InlineData("grpc://svc:50051", "http://svc:50051")]
    [InlineData("grpcs://svc", "https://svc")]
    [InlineData("https://svc", "https://svc")]
    public void Grpc_addresses_normalize_to_http(string input, string expected) =>
        RequestPipeline.NormalizeStreamingScheme(input, RequestKind.Grpc).Should().Be(expected);

    [Fact]
    public async Task Runner_pipeline_refuses_streaming_kinds_with_a_pointer_to_the_editor()
    {
        using var client = new HttpClient();
        var outputs = await RequestPipeline.ExecuteAsync(
            Inputs(new RequestItem { Kind = RequestKind.Grpc, Url = "localhost:1", Grpc = new GrpcConfig { Method = "/a.B/C" } }),
            new HttpExecutor(client), new JintHost());

        outputs.ErrorMessage.Should().Contain("gRPC").And.Contain("request editor");
    }
}
