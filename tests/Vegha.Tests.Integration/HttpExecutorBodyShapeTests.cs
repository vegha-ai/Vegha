using System.Text;
using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Covers the structured-body shapes the Bruno-parity body editor now produces:
/// FormFields → application/x-www-form-urlencoded, MultipartFields → multipart/form-data,
/// FilePath → raw byte stream. All assertions inspect what reaches the wire via WireMock.</summary>
public class HttpExecutorBodyShapeTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpExecutor _exec = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _http = new HttpClient();
        _exec = new HttpExecutor(_http);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _http.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FormFields_SendApplicationXWwwFormUrlEncoded()
    {
        _server.Given(Request.Create()
                .WithPath("/echo").UsingPost()
                .WithHeader("Content-Type", new WireMock.Matchers.RegexMatcher("application/x-www-form-urlencoded.*", true))
                .WithBody(b => b!.Contains("name=alice") && b.Contains("role=admin")))
            .RespondWith(Response.Create().WithStatusCode(200));

        var fields = new[]
        {
            new KeyValuePair<string, string>("name", "alice"),
            new KeyValuePair<string, string>("role", "admin"),
        };

        var result = await _exec.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Post, new Uri($"{_server.Url}/echo"),
            FormFields: fields));

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task MultipartFields_TextOnly_SendMultipartFormData()
    {
        _server.Given(Request.Create()
                .WithPath("/echo").UsingPost()
                .WithHeader("Content-Type", new WireMock.Matchers.RegexMatcher("multipart/form-data;.*boundary=.*", true)))
            .RespondWith(Response.Create().WithStatusCode(200));

        var fields = new[]
        {
            new MultipartField("field1", "hello"),
            new MultipartField("field2", "world"),
        };

        var result = await _exec.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Post, new Uri($"{_server.Url}/echo"),
            MultipartFields: fields));

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task MultipartFields_FilePart_AttachesFileWithCorrectMimeType()
    {
        // Write a small temp file to attach.
        var tmp = Path.Combine(Path.GetTempPath(), $"vegha-mp-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tmp, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG magic
        try
        {
            _server.Given(Request.Create().WithPath("/upload").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));

            var fields = new[]
            {
                new MultipartField("avatar", tmp, Kind: "file"), // content-type auto-detected from .png
            };

            var result = await _exec.ExecuteAsync(new HttpExecutionRequest(
                HttpMethod.Post, new Uri($"{_server.Url}/upload"),
                MultipartFields: fields));

            result.StatusCode.Should().Be(200);

            // Top-level Content-Type must be multipart/form-data with a boundary.
            var logged = _server.LogEntries.Single();
            var contentType = logged.RequestMessage.Headers!["Content-Type"].ToString();
            contentType.Should().Contain("multipart/form-data");
            contentType.Should().Contain("boundary=");

            // The per-part Content-Type for the file row should appear in the raw body
            // bytes between multipart boundaries. WireMock parses binary bodies into
            // BodyAsBytes; decode and search.
            var bodyText = logged.RequestMessage.BodyAsBytes is { Length: > 0 } bytes
                ? Encoding.UTF8.GetString(bytes)
                : logged.RequestMessage.Body ?? string.Empty;
            bodyText.Should().Contain("image/png",
                "the file part's per-part Content-Type header is written into the body between boundaries");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task FilePath_StreamsFileBytes_WithExtensionGuessedMime()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vegha-file-{Guid.NewGuid():N}.json");
        var payload = "{\"hello\":\"world\"}";
        // WriteAllBytes avoids the BOM that File.WriteAllText would emit with Encoding.UTF8.
        await File.WriteAllBytesAsync(tmp, Encoding.UTF8.GetBytes(payload));
        try
        {
            // Echo handler captures the request — we then assert on its body + Content-Type.
            _server.Given(Request.Create().WithPath("/upload").UsingPut())
                .RespondWith(Response.Create().WithStatusCode(200));

            var result = await _exec.ExecuteAsync(new HttpExecutionRequest(
                HttpMethod.Put, new Uri($"{_server.Url}/upload"),
                FilePath: tmp));

            result.StatusCode.Should().Be(200);

            // Inspect the captured request: the body should be the file bytes verbatim and
            // the Content-Type should be the extension-guessed application/json.
            var logged = _server.LogEntries.Single();
            logged.RequestMessage.Body.Should().Be(payload);
            logged.RequestMessage.Headers!["Content-Type"].ToString().Should().Contain("application/json");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task BodyPrecedence_FilePath_BeatsBodyString()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vegha-pri-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(tmp, Encoding.UTF8.GetBytes("from-file"));
        try
        {
            _server.Given(Request.Create().WithPath("/x").UsingPost()
                    .WithBody(b => string.Equals(b, "from-file", StringComparison.Ordinal)))
                .RespondWith(Response.Create().WithStatusCode(200));

            var result = await _exec.ExecuteAsync(new HttpExecutionRequest(
                HttpMethod.Post, new Uri($"{_server.Url}/x"),
                Body: "from-body-string",    // should be ignored
                FilePath: tmp));

            result.StatusCode.Should().Be(200, "FilePath takes precedence over Body");
        }
        finally { File.Delete(tmp); }
    }
}
