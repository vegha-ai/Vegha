using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Scriptless captures, assertions and linked requests through the headless
/// <see cref="RequestPipeline"/> (the engine behind the collection runner and CLI).</summary>
public class ScriptlessPipelineTests : IAsyncLifetime
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

        _server.Given(Request.Create().WithPath("/login").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-Session", "sess-1")
                .WithBody("""{"access_token":"tok-123","user":{"id":7}}"""));
        _server.Given(Request.Create().WithPath("/me").UsingGet().WithHeader("Authorization", "Bearer tok-123"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":7,"name":"Ada","roles":["admin"]}"""));
        _server.Given(Request.Create().WithPath("/fail").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _client.Dispose();
        return Task.CompletedTask;
    }

    private string Url(string path) => _server.Urls[0] + path;

    private RequestItem Login() => new()
    {
        Name = "Login",
        Method = "POST",
        Url = Url("/login"),
        PostResponseVars = { new KvPair("token", "res.body.access_token") },
        PostResponseEnvVars = { new KvPair("session", "res.headers['x-session']") },
        PostResponseGlobalVars = { new KvPair("userId", "res.body.user.id") },
        Assertions = { new KvPair("res.status", "eq 200") },
    };

    private RequestItem Me(params KvPair[] links) => new()
    {
        Name = "Me",
        Method = "GET",
        Url = Url("/me"),
        Headers = { new KvPair("Authorization", "Bearer {{token}}") },
        Assertions =
        {
            new KvPair("res.status", "eq 200"),
            new KvPair("res.body.name", "eq Ada"),
            new KvPair("res.body.roles", "contains admin"),
            new KvPair("res.body.id", "eq {{userId}}"),
        },
        ChainLinks = links.ToList(),
    };

    private static RequestPipeline.Inputs Inputs(Collection c, RequestItem r, ChainContext? chain = null,
        IReadOnlyDictionary<string, string>? env = null, IReadOnlyDictionary<string, string>? runtime = null) =>
        new(c, Array.Empty<Folder>(), r,
            env ?? new Dictionary<string, string>(),
            runtime ?? new Dictionary<string, string>(),
            Chain: chain);

    [Fact]
    public async Task Captures_bucket_by_scope_and_assertions_lead_the_test_list()
    {
        var login = Login();
        var c = new Collection { Name = "c", Requests = { login } };
        var o = await RequestPipeline.ExecuteAsync(Inputs(c, login), _http, _script);

        o.ErrorMessage.Should().BeNull();
        o.RuntimeVariableMutations.Should().Contain("token", "tok-123");
        o.EnvVarMutations.Should().Contain("session", "sess-1");
        o.GlobalVarMutations.Should().Contain("userId", "7");
        o.AssertionCount.Should().Be(1);
        o.Tests.Should().ContainSingle().Which.Should().Match<TestOutcome>(t => t.Passed && t.Name == "res.status eq 200");
    }

    [Fact]
    public async Task Linked_request_runs_first_and_feeds_its_captures_forward()
    {
        var login = Login();
        var me = Me(new KvPair("Login.bru", "when-empty token"));
        var c = new Collection { Name = "c", Requests = { login, me } };
        var ctx = new ChainContext(c, collectionRoot: null);

        var o = await RequestPipeline.ExecuteAsync(Inputs(c, me, ctx), _http, _script);

        o.ErrorMessage.Should().BeNull();
        o.StatusCode.Should().Be(200);
        o.ChainSteps.Should().ContainSingle().Which.Should().Match<ChainStep>(s => s.Ran && s.StatusCode == 200);
        o.ChainSteps[0].Captured.Should().Contain("token", "tok-123");
        o.Tests.Should().HaveCount(4).And.OnlyContain(t => t.Passed);
        o.EnvVarMutations.Should().Contain("session", "sess-1");
        o.GlobalVarMutations.Should().Contain("userId", "7");
        ctx.Cache.Get("Login.bru").Should().NotBeNull();
    }

    [Fact]
    public async Task When_empty_link_is_skipped_when_the_variable_is_set()
    {
        var login = Login();
        var me = Me(new KvPair("Login", "when-empty token"));
        me.Assertions.RemoveAt(3); // userId isn't set without the login run
        var c = new Collection { Name = "c", Requests = { login, me } };

        var o = await RequestPipeline.ExecuteAsync(
            Inputs(c, me, new ChainContext(c, null), runtime: new Dictionary<string, string> { ["token"] = "tok-123" }),
            _http, _script);

        o.ChainSteps.Should().ContainSingle().Which.Ran.Should().BeFalse();
        o.ChainSteps[0].SkipReason.Should().Contain("token");
        o.Tests.Should().OnlyContain(t => t.Passed);
    }

    [Fact]
    public async Task When_stale_link_reuses_a_fresh_cached_response()
    {
        var login = Login();
        var me = Me(new KvPair("Login.bru", "when-stale 10m"));
        var c = new Collection { Name = "c", Requests = { login, me } };
        var ctx = new ChainContext(c, null);

        var first = await RequestPipeline.ExecuteAsync(Inputs(c, me, ctx), _http, _script);
        first.ChainSteps.Single().Ran.Should().BeTrue();

        var second = await RequestPipeline.ExecuteAsync(Inputs(c, me, ctx,
            runtime: new Dictionary<string, string> { ["token"] = "tok-123", ["userId"] = "7" }), _http, _script);
        second.ChainSteps.Single().Ran.Should().BeFalse();
        second.ChainSteps.Single().SkipReason.Should().Contain("old");
    }

    [Fact]
    public async Task Failing_link_stops_the_dependent_request()
    {
        var broken = new RequestItem { Name = "Broken", Method = "POST", Url = Url("/fail") };
        var me = Me(new KvPair("Broken.bru", "always"));
        var c = new Collection { Name = "c", Requests = { broken, me } };

        var o = await RequestPipeline.ExecuteAsync(Inputs(c, me, new ChainContext(c, null)), _http, _script);

        o.StatusCode.Should().Be(0);
        o.ErrorMessage.Should().Contain("Linked request 'Broken' failed: 500");
        o.ChainSteps.Single().Failed.Should().BeTrue();
    }

    [Fact]
    public async Task Cycles_and_missing_links_fail_with_a_readable_message()
    {
        var a = new RequestItem { Name = "A", Method = "POST", Url = Url("/login"), ChainLinks = { new KvPair("B.bru", "always") } };
        var b = new RequestItem { Name = "B", Method = "POST", Url = Url("/login"), ChainLinks = { new KvPair("A.bru", "always") } };
        var c = new Collection { Name = "c", Requests = { a, b } };

        var cyc = await RequestPipeline.ExecuteAsync(Inputs(c, a, new ChainContext(c, null)), _http, _script);
        cyc.ErrorMessage.Should().Contain("cycle").And.Contain("A.bru → B.bru → A.bru");

        var missing = new RequestItem { Name = "M", Method = "GET", Url = Url("/me"), ChainLinks = { new KvPair("nope.bru", "always") } };
        var c2 = new Collection { Name = "c", Requests = { missing } };
        var m = await RequestPipeline.ExecuteAsync(Inputs(c2, missing, new ChainContext(c2, null)), _http, _script);
        m.ErrorMessage.Should().Contain("'nope.bru' was not found");
    }

    [Fact]
    public async Task Chain_links_are_ignored_without_a_chain_context()
    {
        var me = Me(new KvPair("Login.bru", "always"));
        var c = new Collection { Name = "c", Requests = { Login(), me } };
        var o = await RequestPipeline.ExecuteAsync(Inputs(c, me), _http, _script);
        o.ChainSteps.Should().BeEmpty();
        o.StatusCode.Should().NotBe(200); // {{token}} stayed unresolved, so /me didn't match
        o.Tests.Should().Contain(t => !t.Passed && t.Name == "res.status eq 200");
    }
}
