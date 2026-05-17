using System.Net;
using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

public class OAuth2TokenAcquirerTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpClient _http = null!;
    private OAuth2TokenAcquirer _acquirer = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _http = new HttpClient();
        _acquirer = new OAuth2TokenAcquirer(_http);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _http.Dispose();
        return Task.CompletedTask;
    }

    private OAuth2ClientCredentialsConfig Cfg(
        string clientId = "cid",
        string clientSecret = "csec",
        string? scope = null,
        string placement = "body") =>
        new($"{_server.Url}/oauth/token", clientId, clientSecret, scope, placement);

    [Fact]
    public async Task ClientCredentials_HappyPath_ReturnsAccessToken()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("grant_type=client_credentials")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"new-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg());

        r.IsSuccess.Should().BeTrue();
        r.AccessToken.Should().Be("new-token");
        r.FromCache.Should().BeFalse();
        r.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ClientCredentials_BodyPlacement_SendsClientIdAndSecretInForm()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("client_id=cid") && b.Contains("client_secret=csec")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"t1\"}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(placement: "body"));
        r.AccessToken.Should().Be("t1");
    }

    [Fact]
    public async Task ClientCredentials_BasicAuthPlacement_SendsAuthorizationHeader_NotInBody()
    {
        // base64("cid:csec") = "Y2lkOmNzZWM="
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithHeader("Authorization", "Basic Y2lkOmNzZWM=")
                .WithBody(b => !b!.Contains("client_secret"))) // not in body
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"basic-token\"}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(placement: "basic_auth_header"));
        r.AccessToken.Should().Be("basic-token");
    }

    [Fact]
    public async Task ClientCredentials_Scope_IsForwarded()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("scope=read+write") || b.Contains("scope=read%20write") || b.Contains("scope=read write")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"scoped\"}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(scope: "read write"));
        r.AccessToken.Should().Be("scoped");
    }

    [Fact]
    public async Task SecondCall_UsesCachedToken_NoSecondNetworkCall()
    {
        var hits = 0;
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"first\",\"expires_in\":3600}"));
        _server.LogEntriesChanged += (_, _) => hits = _server.LogEntries.Count();

        var first = await _acquirer.AcquireClientCredentialsAsync(Cfg());
        var second = await _acquirer.AcquireClientCredentialsAsync(Cfg());

        first.AccessToken.Should().Be("first");
        second.AccessToken.Should().Be("first");
        second.FromCache.Should().BeTrue();
        _server.LogEntries.Count().Should().Be(1, "second call should hit cache");
    }

    [Fact]
    public async Task DifferentClientId_BypassesCache()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"per-call-id\",\"expires_in\":3600}"));

        await _acquirer.AcquireClientCredentialsAsync(Cfg(clientId: "a"));
        await _acquirer.AcquireClientCredentialsAsync(Cfg(clientId: "b"));

        _server.LogEntries.Count().Should().Be(2);
    }

    [Fact]
    public async Task TokenError_ReturnsFailureWithMessage()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode((int)HttpStatusCode.BadRequest)
                .WithBody("{\"error\":\"invalid_client\"}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg());
        r.IsSuccess.Should().BeFalse();
        r.AccessToken.Should().BeNull();
        r.ErrorMessage.Should().Contain("400");
        r.ErrorMessage.Should().Contain("invalid_client");
    }

    [Fact]
    public async Task ResponseMissingAccessToken_ReturnsFailure()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"hello\":\"world\"}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg());
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("access_token");
    }

    [Fact]
    public async Task ResolvesPlaceholdersInConfig()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("client_id=resolved-id")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"placeholder-resolved\"}"));

        var cfg = new OAuth2ClientCredentialsConfig(
            $"{_server.Url}/oauth/token",
            ClientId: "{{cid}}",
            ClientSecret: "{{secret}}",
            Scope: "{{scope}}");
        var vars = new Dictionary<string, string>
        {
            ["cid"] = "resolved-id",
            ["secret"] = "resolved-secret",
            ["scope"] = "read"
        };

        var r = await _acquirer.AcquireClientCredentialsAsync(cfg, vars);
        r.AccessToken.Should().Be("placeholder-resolved");
    }

    [Fact]
    public async Task InvalidateCache_ForcesReFetch()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"t\",\"expires_in\":3600}"));

        await _acquirer.AcquireClientCredentialsAsync(Cfg());
        _acquirer.InvalidateCache();
        await _acquirer.AcquireClientCredentialsAsync(Cfg());

        _server.LogEntries.Count().Should().Be(2);
    }

    [Fact]
    public async Task EmptyTokenUrlOrClientId_ReturnsFailureWithoutNetworkCall()
    {
        var bad = new OAuth2ClientCredentialsConfig("", "", "");
        var r = await _acquirer.AcquireClientCredentialsAsync(bad);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("required");
        _server.LogEntries.Count().Should().Be(0);
    }

    [Fact]
    public async Task Password_HappyPath_PostsUsernameAndPasswordFields()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("grant_type=password")
                            && b.Contains("username=alice")
                            && b.Contains("password=s3cret")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"pw-token\",\"expires_in\":3600}"));

        var cfg = new OAuth2PasswordConfig(
            $"{_server.Url}/oauth/token", "cid", "csec", "alice", "s3cret");
        var r = await _acquirer.AcquirePasswordAsync(cfg);
        r.IsSuccess.Should().BeTrue();
        r.AccessToken.Should().Be("pw-token");
    }

    [Fact]
    public async Task AuthorizationCode_NonLoopbackCallback_RejectsWithoutLaunchingBrowser()
    {
        var browserCalls = 0;
        _acquirer.BrowserLauncher = _ => { browserCalls++; return Task.CompletedTask; };

        var cfg = new OAuth2AuthorizationCodeConfig(
            AuthorizationUrl: "https://login.test/authorize",
            TokenUrl: $"{_server.Url}/oauth/token",
            ClientId: "cid",
            ClientSecret: "csec",
            CallbackUrl: "https://evil.test/callback");

        var r = await _acquirer.AcquireAuthorizationCodeAsync(cfg);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("loopback");
        browserCalls.Should().Be(0);
    }

    [Fact]
    public async Task AuthorizationCode_HappyPath_OpensBrowser_AndExchangesCodeForToken()
    {
        // The flow: BrowserLauncher fires our fake "user" task that GETs the loopback callback URL
        // with the same ?state= as in the auth URL plus a fake code. The acquirer parses the code
        // and POSTs to the token endpoint. We assert the browser was launched and a token came back.
        var port = FindFreePort();
        var callbackUrl = $"http://127.0.0.1:{port}/oauth/cb";

        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("grant_type=authorization_code")
                            && b.Contains("code=THE_CODE")
                            && b.Contains("code_verifier=")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"ac-token\",\"expires_in\":3600}"));

        _acquirer.BrowserLauncher = authUrl =>
        {
            // Pull the state value out of the auth URL so we can echo it back, and hit the loopback.
            var qs = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query);
            var state = qs["state"];
            var http = new HttpClient();
            // Fire the redirect on a background task so the listener can accept it.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await http.GetAsync($"{callbackUrl}?code=THE_CODE&state={state}");
                http.Dispose();
            });
            return Task.CompletedTask;
        };

        var cfg = new OAuth2AuthorizationCodeConfig(
            AuthorizationUrl: "https://login.test/authorize",
            TokenUrl: $"{_server.Url}/oauth/token",
            ClientId: "cid",
            ClientSecret: "csec",
            CallbackUrl: callbackUrl,
            Scope: "openid",
            UsePkce: true);

        var r = await _acquirer.AcquireAuthorizationCodeAsync(cfg);
        r.IsSuccess.Should().BeTrue(because: r.ErrorMessage ?? string.Empty);
        r.AccessToken.Should().Be("ac-token");
    }

    [Fact]
    public async Task AuthorizationCode_StateMismatch_ReturnsFailure()
    {
        var port = FindFreePort();
        var callbackUrl = $"http://127.0.0.1:{port}/oauth/cb";

        _acquirer.BrowserLauncher = authUrl =>
        {
            var http = new HttpClient();
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                // wrong state
                await http.GetAsync($"{callbackUrl}?code=X&state=NOPE");
                http.Dispose();
            });
            return Task.CompletedTask;
        };

        var cfg = new OAuth2AuthorizationCodeConfig(
            AuthorizationUrl: "https://login.test/authorize",
            TokenUrl: $"{_server.Url}/oauth/token",
            ClientId: "cid",
            ClientSecret: "csec",
            CallbackUrl: callbackUrl,
            State: "EXPECTED");

        var r = await _acquirer.AcquireAuthorizationCodeAsync(cfg);
        r.IsSuccess.Should().BeFalse();
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
