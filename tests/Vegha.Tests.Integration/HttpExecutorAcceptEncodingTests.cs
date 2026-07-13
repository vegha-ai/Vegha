using System.IO.Compression;
using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// Covers Accept-Encoding handling. The handler's AutomaticDecompression injects
/// "gzip, deflate, br" by default (Postman parity); a user-supplied Accept-Encoding header
/// must be sent verbatim — .NET's DecompressionHandler otherwise appends the missing tokens,
/// and gateways that don't support brotli (e.g. Apigee) reject the request with
/// protocol.http.UnsupportedEncoding.
/// </summary>
public class HttpExecutorAcceptEncodingTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpExecutor _executor = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        _executor = new HttpExecutor(_client);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        _client.Dispose();
        return Task.CompletedTask;
    }

    private string? ReceivedAcceptEncoding(string path) =>
        _server.LogEntries
            .Where(e => e.RequestMessage.Path == path)
            .Select(e => e.RequestMessage.Headers != null
                && e.RequestMessage.Headers.TryGetValue("Accept-Encoding", out var v)
                    ? string.Join(", ", v) : null)
            .LastOrDefault();

    [Fact]
    public async Task NoUserHeader_DefaultsToGzipDeflateBr()
    {
        _server.Given(Request.Create().WithPath("/default").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/default")));

        result.StatusCode.Should().Be(200);
        var received = ReceivedAcceptEncoding("/default");
        received.Should().Contain("gzip").And.Contain("deflate").And.Contain("br");
    }

    [Fact]
    public async Task UserHeader_SentVerbatim_BrNotInjected()
    {
        _server.Given(Request.Create().WithPath("/verbatim").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/verbatim"),
            Headers: new[] { new KeyValuePair<string, string>("Accept-Encoding", "gzip, deflate") }));

        result.StatusCode.Should().Be(200);
        var received = ReceivedAcceptEncoding("/verbatim");
        received.Should().Be("gzip, deflate");
    }

    [Fact]
    public async Task UserHeaderIdentity_SentVerbatim_NothingInjected()
    {
        _server.Given(Request.Create().WithPath("/identity").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/identity"),
            Headers: new[] { new KeyValuePair<string, string>("Accept-Encoding", "identity") }));

        result.StatusCode.Should().Be(200);
        ReceivedAcceptEncoding("/identity").Should().Be("identity");
    }

    [Fact]
    public async Task UserHeaderGzip_GzippedResponse_IsStillDecompressed()
    {
        byte[] gzipped;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes("{\"compressed\":true}");
                gz.Write(payload, 0, payload.Length);
            }
            gzipped = ms.ToArray();
        }
        _server.Given(Request.Create().WithPath("/gzipped").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Encoding", "gzip")
                .WithBody(gzipped));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/gzipped"),
            Headers: new[] { new KeyValuePair<string, string>("Accept-Encoding", "gzip") }));

        result.StatusCode.Should().Be(200);
        result.Body.Should().Be("{\"compressed\":true}");
    }

    [Fact]
    public async Task SentRequestText_ShowsEffectiveAcceptEncoding()
    {
        _server.Given(Request.Create().WithPath("/sent").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        // Auto-injected default must be visible in the "Sent" view (it goes on the wire).
        var auto = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/sent")));
        auto.SentRequestText.Should().Contain("Accept-Encoding: gzip, deflate, br");

        // User-supplied header renders as-is, exactly once.
        var manual = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/sent"),
            Headers: new[] { new KeyValuePair<string, string>("Accept-Encoding", "gzip") }));
        manual.SentRequestText.Should().Contain("Accept-Encoding: gzip");
        manual.SentRequestText.Should().NotContain("br");
    }
}
