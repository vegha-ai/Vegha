using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>End-to-end tests for the headless single-request execution pipeline. Asserts the
/// compose → script → http → script chain is wired correctly. Auth / body / interpolation
/// coverage is shallow on purpose — those have dedicated unit tests elsewhere; here we just
/// confirm the pipeline plumbs them through.</summary>
public class RequestPipelineTests : IAsyncLifetime
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

    private RequestPipeline.Inputs BuildInputs(
        string method, string url,
        BodyConfig? body = null, AuthConfig? auth = null,
        IList<KvPair>? headers = null, IList<KvPair>? queryParams = null,
        string? preScript = null, string? testsScript = null,
        string? postScript = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        IReadOnlyDictionary<string, string>? iterVars = null)
    {
        var request = new RequestItem
        {
            Name = "test", Method = method, Url = url,
            Headers = headers ?? new List<KvPair>(),
            Params = queryParams ?? new List<KvPair>(),
            Body = body ?? new BodyConfig(),
            Auth = auth,
            PreRequestScript = preScript,
            PostResponseScript = postScript,
            Tests = testsScript,
        };
        var collection = new Collection { Name = "c", Requests = new List<RequestItem> { request } };
        return new RequestPipeline.Inputs(
            collection,
            Array.Empty<Folder>(),
            request,
            envVars ?? new Dictionary<string, string>(),
            iterVars ?? new Dictionary<string, string>());
    }

    [Fact]
    public async Task Get_returns_200_and_passes_body_through()
    {
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("pong"));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/ping");
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.ErrorMessage.Should().BeNull();
        result.StatusCode.Should().Be(200);
        result.ResponseBody.Should().Be("pong");
    }

    [Fact]
    public async Task GraphQL_MultiOperationDocument_SendsFirstOperationName()
    {
        _server.Given(Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"data\":{}}"));

        var body = new BodyConfig
        {
            Mode = BodyMode.GraphQL,
            GraphQLQuery = "query First { a } mutation Second { b }",
            GraphQLVariables = "{}",
        };
        var inputs = BuildInputs("POST", _server.Urls[0] + "/graphql", body: body);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
        var received = _server.LogEntries
            .Last(e => e.RequestMessage.Path == "/graphql").RequestMessage.Body;
        received.Should().Contain("\"operationName\":\"First\"");
    }

    [Fact]
    public async Task GraphQL_Subscription_FailsWithClearRunnerError()
    {
        var body = new BodyConfig
        {
            Mode = BodyMode.GraphQL,
            GraphQLQuery = "subscription OnTick { tick }",
        };
        var inputs = BuildInputs("POST", _server.Urls[0] + "/graphql", body: body);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(0);
        result.ErrorMessage.Should().Contain("subscriptions are not supported in the collection runner");
    }

    [Fact]
    public async Task GraphQL_SingleOperation_OmitsOperationName()
    {
        _server.Given(Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"data\":{}}"));

        var body = new BodyConfig
        {
            Mode = BodyMode.GraphQL,
            GraphQLQuery = "query Only { a }",
        };
        var inputs = BuildInputs("POST", _server.Urls[0] + "/graphql", body: body);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
        var received = _server.LogEntries
            .Last(e => e.RequestMessage.Path == "/graphql").RequestMessage.Body;
        received.Should().NotContain("operationName");
        received.Should().Contain("\"query\":");
    }

    [Fact]
    public async Task IterationVariables_resolve_in_url()
    {
        _server.Given(Request.Create().WithPath("/users/42").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/users/{{userId}}",
            iterVars: new Dictionary<string, string> { ["userId"] = "42" });
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
        result.ResolvedUrl.Should().EndWith("/users/42");
    }

    [Fact]
    public async Task IterationVariables_override_environment()
    {
        _server.Given(Request.Create().WithPath("/who").WithParam("name", "iter").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/who",
            queryParams: new List<KvPair> { new("name", "{{name}}") },
            envVars: new Dictionary<string, string> { ["name"] = "env" },
            iterVars: new Dictionary<string, string> { ["name"] = "iter" });
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task PreRequestScript_runtime_var_is_visible_to_url_interpolation()
    {
        _server.Given(Request.Create().WithPath("/").WithParam("token", "abc123").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/",
            queryParams: new List<KvPair> { new("token", "{{token}}") },
            preScript: "bru.setVar('token', 'abc123');");
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.ErrorMessage.Should().BeNull();
        result.StatusCode.Should().Be(200);
        result.RuntimeVariableMutations.Should().ContainKey("token").WhoseValue.Should().Be("abc123");
    }

    [Fact]
    public async Task PostResponse_setEnvVar_surfaces_in_EnvVarMutations()
    {
        // Mirrors the token-extraction pattern: POST a token endpoint, pull access_token out
        // of the JSON body in the post-response script, and stash it via bru.setEnvVar. The
        // runner threads EnvVarMutations forward so {{access_token}} resolves in the next request.
        _server.Given(Request.Create().WithPath("/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("{\"access_token\":\"zVd7kPHFs2y2AwDx17e2TEa4ATDA\"}"));

        var inputs = BuildInputs("POST", _server.Urls[0] + "/token",
            postScript: """
                var jsonData = JSON.parse(res.getBodyAsText());
                if (jsonData) {
                    bru.setEnvVar("access_token", jsonData.access_token);
                }
                """);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.ErrorMessage.Should().BeNull();
        result.StatusCode.Should().Be(200);
        result.EnvVarMutations.Should().ContainKey("access_token")
            .WhoseValue.Should().Be("zVd7kPHFs2y2AwDx17e2TEa4ATDA");
    }

    [Fact]
    public async Task TestsScript_runs_and_results_surface_in_outputs()
    {
        _server.Given(Request.Create().WithPath("/data").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("hello"));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/data",
            testsScript: """
                test('status is 200', () => { expect(res.getStatus()).to.equal(200); });
                test('body is hello', () => { expect(res.getBodyAsText()).to.equal('hello'); });
                test('intentional failure', () => { expect(res.getStatus()).to.equal(500); });
            """);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
        result.Tests.Should().HaveCount(3);
        result.PassedTests.Should().Be(2);
        result.FailedTests.Should().Be(1);
    }

    [Fact]
    public async Task BearerAuth_emits_Authorization_header()
    {
        _server.Given(Request.Create().WithPath("/secure")
                .WithHeader("Authorization", "Bearer secret-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("yes"));

        var auth = new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = "secret-token" },
        };
        var inputs = BuildInputs("GET", _server.Urls[0] + "/secure", auth: auth);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task UnsupportedAuth_returns_clear_error_without_calling_HTTP()
    {
        _server.Given(Request.Create().WithPath("/anything").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(200));

        var auth = new AuthConfig
        {
            Type = AuthType.OAuth2,
            Parameters = new Dictionary<string, string>(),
        };
        var inputs = BuildInputs("GET", _server.Urls[0] + "/anything", auth: auth);
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(0);
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("OAuth2").And.Contain("not supported");
    }

    [Fact]
    public async Task JsonBody_is_sent_with_content_type()
    {
        _server.Given(Request.Create().WithPath("/post").UsingPost()
                .WithHeader("Content-Type", "application/json")
                .WithBody(b => b != null && b.Contains("\"name\":\"avery\"")))
            .RespondWith(Response.Create().WithStatusCode(201));

        var inputs = BuildInputs("POST", _server.Urls[0] + "/post",
            body: new BodyConfig { Mode = BodyMode.Json, Content = "{\"name\":\"avery\"}" });
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task FormUrlEncodedBody_encodes_pairs_and_sets_content_type()
    {
        _server.Given(Request.Create().WithPath("/form").UsingPost()
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithBody(b => b != null && b.Contains("a=1") && b.Contains("b=two%20words")))
            .RespondWith(Response.Create().WithStatusCode(204));

        var inputs = BuildInputs("POST", _server.Urls[0] + "/form",
            body: new BodyConfig
            {
                Mode = BodyMode.FormUrlEncoded,
                FormData = new List<KvPair> { new("a", "1"), new("b", "two words") },
            });
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        result.StatusCode.Should().Be(204);
    }

    [Fact]
    public async Task Cancellation_stops_in_flight_request()
    {
        _server.Given(Request.Create().WithPath("/slow").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        var inputs = BuildInputs("GET", _server.Urls[0] + "/slow");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await RequestPipeline.ExecuteAsync(inputs, _http, _script, cts.Token);

        result.ErrorMessage.Should().NotBeNull();
    }
}
