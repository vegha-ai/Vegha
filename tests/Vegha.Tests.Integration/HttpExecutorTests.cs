using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

public class HttpExecutorTests : IAsyncLifetime
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

    [Fact]
    public async Task GetRequest_Returns200_WithBody()
    {
        _server.Given(Request.Create().WithPath("/users/42").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":42,\"name\":\"avery\"}"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri($"{_server.Url}/users/42")));

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Body.Should().Contain("\"id\":42");
        result.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(0);
        result.Headers.Should().Contain(h => h.Key == "Content-Type" && h.Value.Contains("application/json"));
    }

    [Fact]
    public async Task PostRequest_SendsBody_AndContentType()
    {
        _server.Given(Request.Create()
                .WithPath("/echo")
                .UsingPost()
                .WithBody("{\"hello\":\"world\"}"))
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithBody("created"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Post,
            new Uri($"{_server.Url}/echo"),
            Body: "{\"hello\":\"world\"}",
            ContentType: "application/json"));

        result.StatusCode.Should().Be(201);
        result.Body.Should().Be("created");
    }

    [Fact]
    public async Task NotFound_DoesNotThrow_AndReturns404()
    {
        _server.Given(Request.Create().WithPath("/missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("not found"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri($"{_server.Url}/missing")));

        result.StatusCode.Should().Be(404);
        result.IsSuccess.Should().BeFalse();
        result.IsTransportError.Should().BeFalse();
        result.Body.Should().Be("not found");
    }

    [Fact]
    public async Task RequestHeaders_AreSent()
    {
        _server.Given(Request.Create()
                .WithPath("/auth")
                .UsingGet()
                .WithHeader("X-Tenant", "acme"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri($"{_server.Url}/auth"),
            Headers: new[] { new KeyValuePair<string, string>("X-Tenant", "acme") }));

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task CookieJar_PersistsAcrossRequests()
    {
        // /login sets a cookie; /me echoes it back via Set-Cookie matcher → 200 only when cookie present.
        _server.Given(Request.Create().WithPath("/login").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Set-Cookie", "session=tok-1; Path=/")
                .WithBody("ok"));
        _server.Given(Request.Create()
                .WithPath("/me")
                .UsingGet()
                .WithCookie("session", "tok-1"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("identified"));
        _server.Given(Request.Create().WithPath("/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("anonymous"));

        // First call: login (default UseCookies=true gives shared jar)
        var login = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Post,
            new Uri($"{_server.Url}/login"),
            Options: new Vegha.Core.Requests.HttpRequestOptions(UseCookies: true)));
        login.StatusCode.Should().Be(200);

        // Second call: /me — cookie must ride along from the shared jar
        var me = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri($"{_server.Url}/me"),
            Options: new Vegha.Core.Requests.HttpRequestOptions(UseCookies: true)));
        me.StatusCode.Should().Be(200);
        me.Body.Should().Be("identified");
    }

    [Fact]
    public async Task UseCookiesFalse_DoesNotSendStoredCookies()
    {
        _server.Given(Request.Create().WithPath("/login").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Set-Cookie", "session=tok-2; Path=/"));
        _server.Given(Request.Create()
                .WithPath("/me").UsingGet().WithCookie("session", "tok-2"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("identified"));
        _server.Given(Request.Create().WithPath("/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("anonymous"));

        await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Post, new Uri($"{_server.Url}/login"),
            Options: new Vegha.Core.Requests.HttpRequestOptions(UseCookies: true)));

        var meNoCookies = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get, new Uri($"{_server.Url}/me"),
            Options: new Vegha.Core.Requests.HttpRequestOptions(UseCookies: false)));

        meNoCookies.StatusCode.Should().Be(401);
        meNoCookies.Body.Should().Be("anonymous");
    }

    [Fact]
    public async Task Unreachable_Host_Returns_TransportError()
    {
        // Bind to an unused localhost port that's almost certainly closed.
        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri("http://127.0.0.1:1")));

        result.IsTransportError.Should().BeTrue();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.StatusCode.Should().Be(0);
    }

    [Fact]
    public async Task Timing_FirstRequest_RecordsConnectAndContentPhases()
    {
        _server.Given(Request.Create().WithPath("/timing").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("ok"));

        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri($"{_server.Url}/timing")));

        result.IsSuccess.Should().BeTrue();
        result.Timing.Should().NotBeNull();
        var t = result.Timing!;

        // Localhost DNS may register as zero (literal IP path), so DNS isn't asserted.
        // Connect and TTFB always run on the very first request.
        t.ConnectMs.Should().BeGreaterOrEqualTo(0);
        t.TtfbMs.Should().BeGreaterOrEqualTo(0);
        t.TotalMs.Should().BeGreaterThan(0);
        // Plain HTTP — TLS phase must be 0.
        t.TlsMs.Should().Be(0);
        // Total should be at least the sum of the phases that were measured (allow scheduler drift).
        t.TotalMs.Should().BeGreaterOrEqualTo(t.SumOfPhases - 5);
    }

    [Fact]
    public async Task Timing_SecondRequest_ReusesConnection_NoConnectPhase()
    {
        _server.Given(Request.Create().WithPath("/keepalive").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("hi"));

        var url = new Uri($"{_server.Url}/keepalive");

        // Warm up the connection pool.
        var first = await _executor.ExecuteAsync(new HttpExecutionRequest(HttpMethod.Get, url));
        first.IsSuccess.Should().BeTrue();

        // Second request should reuse the pooled connection — DNS/Connect/TLS all 0.
        var second = await _executor.ExecuteAsync(new HttpExecutionRequest(HttpMethod.Get, url));
        second.IsSuccess.Should().BeTrue();

        var t = second.Timing!;
        t.DnsMs.Should().Be(0);
        t.ConnectMs.Should().Be(0);
        t.TlsMs.Should().Be(0);
        // TTFB is always measured.
        t.TtfbMs.Should().BeGreaterOrEqualTo(0);
        t.TotalMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Timing_TransportFailure_StillReturnsTimingObject()
    {
        var result = await _executor.ExecuteAsync(new HttpExecutionRequest(
            HttpMethod.Get,
            new Uri("http://127.0.0.1:1")));

        result.IsTransportError.Should().BeTrue();
        result.Timing.Should().NotBeNull();
        result.Timing!.TotalMs.Should().BeGreaterOrEqualTo(0);
    }
}
