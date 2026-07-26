using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>
/// Auth defined on a collection or folder must reach the wire exactly as request-level auth
/// does — for every scheme, not just the three the applier handles inline.
///
/// This is the regression guard for the bug these tests were written against: the send path
/// used to branch on the REQUEST's own auth type, so an inherited OAuth2 / SigV4 / Digest /
/// NTLM / OAuth1 / WSSE block composed correctly and then contributed nothing, producing a
/// silently unauthenticated request. Everything now runs off the composed
/// <see cref="AuthConfig"/> via <see cref="AuthPreparer"/>.
/// </summary>
public class InheritedAuthTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpExecutor _http = null!;
    private HttpClient _client = null!;
    private readonly JintHost _script = new();

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        _http = new HttpExecutor(_client);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _client.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A request that declares no auth of its own, under a collection (and optional
    /// folder chain) that does.</summary>
    private RequestPipeline.Inputs Inherit(
        AuthConfig? collectionAuth,
        AuthConfig? requestAuth = null,
        IReadOnlyList<Folder>? folderChain = null,
        string path = "/secure")
    {
        var request = new RequestItem
        {
            Name = "test",
            Method = "GET",
            Url = _server.Urls[0] + path,
            Auth = requestAuth,
        };
        var collection = new Collection
        {
            Name = "c",
            Auth = collectionAuth,
            Requests = new List<RequestItem> { request },
        };
        return new RequestPipeline.Inputs(
            collection,
            folderChain ?? Array.Empty<Folder>(),
            request,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }

    private static AuthConfig Auth(AuthType type, params (string Key, string Value)[] parameters) =>
        new()
        {
            Type = type,
            Parameters = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
        };

    // ---- Schemes that already worked, kept as guardrails ----

    [Fact]
    public async Task Collection_bearer_reaches_the_request()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer col-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(Auth(AuthType.Bearer, ("token", "col-token"))), _http, _script);

        result.ErrorMessage.Should().BeNull();
        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Request_level_auth_beats_the_collection()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer own-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(
                collectionAuth: Auth(AuthType.Bearer, ("token", "col-token")),
                requestAuth: Auth(AuthType.Bearer, ("token", "own-token"))),
            _http, _script);

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Nearest_folder_beats_the_collection()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer folder-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var folder = new Folder
        {
            Name = "inner",
            Auth = Auth(AuthType.Bearer, ("token", "folder-token")),
        };

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(
                collectionAuth: Auth(AuthType.Bearer, ("token", "col-token")),
                folderChain: new[] { folder }),
            _http, _script);

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Explicit_inherit_on_the_request_falls_through_to_the_collection()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer col-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(
                collectionAuth: Auth(AuthType.Bearer, ("token", "col-token")),
                requestAuth: new AuthConfig { Type = AuthType.Inherit }),
            _http, _script);

        result.StatusCode.Should().Be(200);
    }

    // ---- Schemes that used to be silently dropped when inherited ----

    [Fact]
    public async Task Collection_oauth2_acquires_a_token_and_sends_it()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("{\"access_token\":\"inherited-token\",\"expires_in\":3600}"));
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer inherited-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var inputs = Inherit(Auth(AuthType.OAuth2,
            ("grant_type", "client_credentials"),
            ("access_token_url", _server.Urls[0] + "/oauth/token"),
            ("client_id", "cid"),
            ("client_secret", "csec"),
            // A fresh cache slot per test run, so a sibling test's token can't satisfy this one.
            ("token_id", "inherited-oauth2-test")));

        using var oauthClient = new HttpClient();
        var result = await RequestPipeline.ExecuteAsync(
            inputs, _http, _script, default, new OAuth2TokenAcquirer(oauthClient));

        result.ErrorMessage.Should().BeNull();
        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Collection_awsv4_signs_the_request()
    {
        _server.Given(Request.Create().WithPath("/secure").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(Auth(AuthType.AwsV4,
                ("accessKeyId", "AKIDEXAMPLE"),
                ("secretAccessKey", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"),
                ("region", "us-east-1"),
                ("service", "execute-api"))),
            _http, _script);

        result.StatusCode.Should().Be(200);
        result.RequestHeaders.Should().Contain(h =>
            h.Key == "Authorization" && h.Value.StartsWith("AWS4-HMAC-SHA256"));
    }

    [Fact]
    public async Task Collection_oauth1_signs_the_request()
    {
        _server.Given(Request.Create().WithPath("/secure").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(Auth(AuthType.OAuth1,
                ("consumerKey", "ck"),
                ("consumerSecret", "cs"),
                ("signatureMethod", "HMAC-SHA1"))),
            _http, _script);

        result.StatusCode.Should().Be(200);
        result.RequestHeaders.Should().Contain(h =>
            h.Key == "Authorization" && h.Value.StartsWith("OAuth "));
    }

    [Fact]
    public async Task Collection_wsse_adds_the_username_token()
    {
        _server.Given(Request.Create().WithPath("/secure").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(Auth(AuthType.Wsse, ("username", "alice"), ("password", "s3cret"))),
            _http, _script);

        result.StatusCode.Should().Be(200);
        result.RequestHeaders.Should().Contain(h =>
            h.Key == "X-WSSE" && h.Value.Contains("Username=\"alice\"") && h.Value.Contains("PasswordDigest="));
    }

    [Fact]
    public async Task Collection_digest_answers_the_401_challenge()
    {
        // First leg: unauthenticated → 401 + challenge. Second: carries the digest response.
        _server.Given(Request.Create().WithPath("/secure").UsingGet()
                .WithHeader("Authorization", "*Digest*"))
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/secure").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("WWW-Authenticate",
                    "Digest realm=\"test\", qop=\"auth\", nonce=\"abc123\", opaque=\"xyz\""));

        var result = await RequestPipeline.ExecuteAsync(
            Inherit(Auth(AuthType.Digest, ("username", "alice"), ("password", "s3cret"))),
            _http, _script);

        result.RequestHeaders.Should().Contain(h =>
            h.Key == "Authorization" && h.Value.StartsWith("Digest ") && h.Value.Contains("username=\"alice\""));
    }

    [Fact]
    public async Task Collection_ntlm_yields_credentials_for_the_handler()
    {
        // NTLM is negotiated by the handler rather than a precomputed header, so the contract
        // is "the composed config produces a NetworkCredential" — asserted directly.
        var prepared = await AuthPreparer.PrepareAsync(
            Auth(AuthType.Ntlm, ("username", "alice"), ("password", "s3cret"), ("domain", "CORP")),
            "https://example.test/secure",
            new Dictionary<string, string>());

        prepared.IsError.Should().BeFalse();
        prepared.NtlmCredential.Should().NotBeNull();
        prepared.NtlmCredential!.UserName.Should().Be("alice");
        prepared.NtlmCredential.Domain.Should().Be("CORP");
    }

    // ---- Variable resolution works the same at parent scope ----

    [Fact]
    public async Task Collection_auth_resolves_variables()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer resolved-value").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var request = new RequestItem { Name = "t", Method = "GET", Url = _server.Urls[0] + "/secure" };
        var collection = new Collection
        {
            Name = "c",
            Auth = Auth(AuthType.Bearer, ("token", "{{jwt}}")),
            Requests = new List<RequestItem> { request },
        };
        var inputs = new RequestPipeline.Inputs(
            collection, Array.Empty<Folder>(), request,
            new Dictionary<string, string> { ["jwt"] = "resolved-value" },
            new Dictionary<string, string>());

        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
    }
}
