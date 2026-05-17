using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class BruApiExtendedTests
{
    private readonly JintHost _host;

    public BruApiExtendedTests()
    {
        _host = new JintHost(subRequestExecutor: new FakeBruExecutor());
    }

    private static readonly Dictionary<string, string> NoVars = new();

    [Fact]
    public void SetEnvVar_ScriptMutation_VisibleInEnvVarMutations()
    {
        var r = _host.RunPreRequest("bru.setEnvVar('apiKey', 'NEW123');", NoVars);
        r.IsSuccess.Should().BeTrue();
        r.EnvVarMutations.Should().ContainKey("apiKey");
        r.EnvVarMutations["apiKey"].Should().Be("NEW123");
    }

    [Fact]
    public void SetNextRequest_RecordsTargetName_OnRunState()
    {
        var r = _host.RunPreRequest("bru.setNextRequest('createOrder');", NoVars);
        r.IsSuccess.Should().BeTrue();
        r.RunState.NextRequestName.Should().Be("createOrder");
    }

    [Fact]
    public void Runner_SkipRequest_StopExecution_FlagsRunState()
    {
        var r = _host.RunPreRequest(
            """
            bru.runner.skipRequest();
            bru.runner.stopExecution();
            """, NoVars);
        r.RunState.SkipRequest.Should().BeTrue();
        r.RunState.StopExecution.Should().BeTrue();
    }

    [Fact]
    public void SendRequest_DelegatesToHostExecutor_ReturnsResultToScript()
    {
        var r = _host.RunPreRequest(
            """
            var resp = bru.sendRequest({ method: 'GET', url: 'https://api.test/health' });
            bru.setVar('childStatus', String(resp.Status));
            bru.setVar('childBody', resp.Body);
            """, NoVars);

        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables["childStatus"].Should().Be("200");
        r.RuntimeVariables["childBody"].Should().Be("{\"ok\":true}");
    }

    [Fact]
    public void Req_PreRequestScript_CanMutateMethodAndHeaders()
    {
        var req = new RequestApi("GET", "https://api.test/users", body: null,
            new[] { new KeyValuePair<string, string>("X-A", "1") });

        var r = _host.RunPreRequest(
            """
            req.setMethod('POST');
            req.setHeader('X-Trace', 'abc');
            req.removeHeader('X-A');
            """, NoVars, request: req);

        r.IsSuccess.Should().BeTrue();
        req.Method.Should().Be("POST");
        req.Headers.Should().ContainKey("X-Trace").WhoseValue.Should().Be("abc");
        req.Headers.Should().NotContainKey("X-A");
    }

    private sealed class FakeBruExecutor : IBruRequestExecutor
    {
        public BruRequestResult Send(BruRequestOptions options) =>
            new(200, "OK", "{\"ok\":true}", 5,
                new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
                Error: null);
    }
}
