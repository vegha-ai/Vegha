using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// Pins the Content-Type behaviour. Background: a regression earlier sent two values
/// concatenated ("application/json; charset=utf-8, application/json") because
/// StringContent appended a charset and the user's explicit header was also added —
/// servers that strict-parse Content-Type rejected the body.
/// </summary>
public class HttpExecutorContentTypeTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private static HttpExecutor NewExecutor() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

    [Fact]
    public async Task UserContentType_WinsOverRuntimeDefault_NoDuplicates()
    {
        _server.Given(Request.Create().WithPath("/echo").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var executor = NewExecutor();
        var headers = new[]
        {
            new KeyValuePair<string, string>("Content-Type", "application/json"),
        };
        var req = new HttpExecutionRequest(
            HttpMethod.Post,
            new Uri(_server.Urls[0] + "/echo"),
            Headers: headers,
            Body: """{"k":"v"}""",
            ContentType: "application/json");

        var result = await executor.ExecuteAsync(req);
        result.IsSuccess.Should().BeTrue();

        // Inspect the captured Sent text — the regression we're guarding against shows up
        // as "application/json; charset=utf-8, application/json" (comma-joined values).
        // Scope the comma check to the Content-Type line: other headers (Accept-Encoding)
        // legitimately carry comma-separated values.
        result.SentRequestText.Should().NotBeNull();
        var contentTypeLine = result.SentRequestText!
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
        contentTypeLine.Should().NotBeNull();
        contentTypeLine!.Should().NotContain(",", because: "Content-Type must be a single value, not comma-joined");
        result.SentRequestText.Should().Contain("Content-Type: application/json");
        // No charset suffix — the user's literal header wins.
        result.SentRequestText.Should().NotContain("charset=utf-8");
    }

    [Fact]
    public async Task NoUserContentType_AutoAppendsCharset_StillSingleValue()
    {
        // No user header → runtime appends charset=utf-8 (standard for JSON), still as a
        // single Content-Type entry — no comma-joined duplication.
        _server.Given(Request.Create().WithPath("/echo2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var executor = NewExecutor();
        var req = new HttpExecutionRequest(
            HttpMethod.Post,
            new Uri(_server.Urls[0] + "/echo2"),
            Headers: Array.Empty<KeyValuePair<string, string>>(),
            Body: """{"k":"v"}""",
            ContentType: "application/json");

        var result = await executor.ExecuteAsync(req);
        result.IsSuccess.Should().BeTrue();
        var contentTypeLine = result.SentRequestText!
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
        contentTypeLine.Should().NotBeNull();
        contentTypeLine!.Should().Contain("application/json")
            .And.Contain("charset=utf-8")
            .And.NotContain(",");
    }

    [Fact]
    public async Task UserContentType_WithExplicitCharset_PreservesIt()
    {
        _server.Given(Request.Create().WithPath("/echo3").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var executor = NewExecutor();
        var headers = new[]
        {
            new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"),
        };
        var req = new HttpExecutionRequest(
            HttpMethod.Post,
            new Uri(_server.Urls[0] + "/echo3"),
            Headers: headers,
            Body: """{"k":"v"}""",
            ContentType: "application/json");

        var result = await executor.ExecuteAsync(req);
        result.IsSuccess.Should().BeTrue();
        result.SentRequestText!.Should().Contain("Content-Type: application/json; charset=utf-8");
        var contentTypeLine = result.SentRequestText!
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
        contentTypeLine!.Should().NotContain(",", because: "still a single header value");
    }
}
