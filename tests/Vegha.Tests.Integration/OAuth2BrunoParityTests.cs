using System.Web;
using Vegha.Core.Requests;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Covers the Bruno-parity additions to <see cref="OAuth2TokenAcquirer"/>: token-id
/// cache isolation, token source selection, refresh_token grant flow, additional parameters
/// in body / headers / queryparams, and the new ClearCache-by-token-id hook.</summary>
public class OAuth2BrunoParityTests : IAsyncLifetime
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
        string? scope = null,
        string tokenId = "credentials",
        string tokenSource = "access_token",
        IReadOnlyList<OAuth2AdditionalParam>? additional = null,
        IReadOnlyList<OAuth2AdditionalParam>? refresh = null) =>
        new($"{_server.Url}/oauth/token", clientId, "csec",
            Scope: scope,
            AdditionalParameters: additional,
            TokenId: tokenId,
            TokenSource: tokenSource,
            RefreshParameters: refresh);

    // =============== TokenId isolation ===============

    [Fact]
    public async Task DifferentTokenIds_ProduceIndependentCacheSlots()
    {
        // Same client_id / scope, but different TokenId — each call should hit the token
        // endpoint fresh on the first acquire (no cache cross-contamination).
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"first\",\"expires_in\":3600}"));

        var a = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "alice"));
        var b = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "bob"));

        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        a.FromCache.Should().BeFalse();
        b.FromCache.Should().BeFalse("the bob slot was empty even though alice's slot is hot");

        // The second acquire for alice should now hit the cache.
        var a2 = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "alice"));
        a2.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidateCacheForTokenId_OnlyClearsMatchingSlots()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"hit\",\"expires_in\":3600}"));

        await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "alice"));
        await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "bob"));

        _acquirer.InvalidateCacheForTokenId("alice");

        var aliceAfter = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "alice"));
        var bobAfter   = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenId: "bob"));

        aliceAfter.FromCache.Should().BeFalse("alice's slot was invalidated");
        bobAfter.FromCache.Should().BeTrue("bob's slot was untouched");
    }

    // =============== Token source ===============

    [Fact]
    public async Task TokenSource_IdToken_PicksIdTokenFromResponse()
    {
        // IdP returns both access_token and id_token — Bruno's "Token Source = id_token"
        // should hand back the id_token as the bearer.
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"a.token\",\"id_token\":\"i.token\",\"expires_in\":3600}"));

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(tokenSource: "id_token"));

        r.IsSuccess.Should().BeTrue();
        r.AccessToken.Should().Be("i.token");
    }

    // =============== Additional parameters ===============

    [Fact]
    public async Task AdditionalParameters_SendInBody_MergeIntoFormPost()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost()
                .WithBody(b => b!.Contains("audience=https%3A%2F%2Fapi.example.com")
                            && b.Contains("resource=foo")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"ok\",\"expires_in\":3600}"));

        var extras = new[]
        {
            new OAuth2AdditionalParam("audience", "https://api.example.com", "body"),
            new OAuth2AdditionalParam("resource", "foo", "body"),
        };

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(additional: extras));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AdditionalParameters_SendInQuery_AppendToTokenUrl()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost()
                .WithParam("tenant", "acme"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"ok\",\"expires_in\":3600}"));

        var extras = new[] { new OAuth2AdditionalParam("tenant", "acme", "queryparams") };

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(additional: extras));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AdditionalParameters_SendInHeaders_AttachToTokenRequest()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost()
                .WithHeader("X-Tenant", "acme"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"ok\",\"expires_in\":3600}"));

        var extras = new[] { new OAuth2AdditionalParam("X-Tenant", "acme", "headers") };

        var r = await _acquirer.AcquireClientCredentialsAsync(Cfg(additional: extras));
        r.IsSuccess.Should().BeTrue();
    }

    // =============== Refresh flow ===============

    [Fact]
    public async Task RefreshToken_FlowFires_WhenCachedSlotIsStaleButHasRefreshToken()
    {
        // First call: returns access_token + refresh_token + very short TTL so the next
        // call sees a stale slot. The acquirer should then POST grant_type=refresh_token
        // rather than starting a new client_credentials grant.
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost()
                .WithBody(b => b!.Contains("grant_type=client_credentials")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"first\",\"refresh_token\":\"r-1\",\"expires_in\":0}"));

        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost()
                .WithBody(b => b!.Contains("grant_type=refresh_token")
                            && b.Contains("refresh_token=r-1")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"second\",\"refresh_token\":\"r-2\",\"expires_in\":3600}"));

        var r1 = await _acquirer.AcquireClientCredentialsAsync(Cfg());
        r1.AccessToken.Should().Be("first");

        // Wait a tick so the very-short TTL expires (TTL=0 - guard window → stale immediately).
        await Task.Delay(50);

        var r2 = await _acquirer.AcquireClientCredentialsAsync(Cfg());
        r2.AccessToken.Should().Be("second",
            "the cached refresh_token should drive a refresh_token grant rather than a full re-auth");
    }
}
