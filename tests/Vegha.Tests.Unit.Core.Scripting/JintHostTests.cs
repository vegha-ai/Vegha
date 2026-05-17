using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class JintHostTests
{
    private readonly JintHost _host = new();
    private static readonly Dictionary<string, string> NoVars = new();

    [Fact]
    public void EmptyScript_Succeeds_NoMutations()
    {
        var r = _host.RunPreRequest(string.Empty, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables.Should().BeEmpty();
    }

    [Fact]
    public void NullScript_Succeeds_NoMutations()
    {
        var r = _host.RunPreRequest(null!, NoVars);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void SetVar_Mutation_AppearsInResult()
    {
        var r = _host.RunPreRequest("bru.setVar('userId', '42');", NoVars);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables.Should().ContainKey("userId").WhoseValue.Should().Be("42");
    }

    [Fact]
    public void GetEnvVar_ReadsActiveEnvironment()
    {
        var env = new Dictionary<string, string> { ["host"] = "https://api.acme.io" };
        var r = _host.RunPreRequest("bru.setVar('resolvedHost', bru.getEnvVar('host'));", env);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables["resolvedHost"].Should().Be("https://api.acme.io");
    }

    [Fact]
    public void GetVar_FallsThroughToEnv_ThenCollection()
    {
        var env = new Dictionary<string, string> { ["a"] = "from-env" };
        var col = new Dictionary<string, string> { ["b"] = "from-collection" };
        var script = """
            bru.setVar('aOut', bru.getVar('a'));
            bru.setVar('bOut', bru.getVar('b'));
            bru.setVar('cOut', bru.getVar('c') || 'default');
            """;
        var r = _host.RunPreRequest(script, env, col);
        r.RuntimeVariables["aOut"].Should().Be("from-env");
        r.RuntimeVariables["bOut"].Should().Be("from-collection");
        r.RuntimeVariables["cOut"].Should().Be("default");
    }

    [Fact]
    public void RequestVars_SeedRuntime()
    {
        var requestVars = new Dictionary<string, string> { ["initial"] = "hello" };
        var r = _host.RunPreRequest(
            "bru.setVar('echoed', bru.getVar('initial'));",
            NoVars, null, requestVars);
        r.RuntimeVariables["echoed"].Should().Be("hello");
        r.RuntimeVariables["initial"].Should().Be("hello"); // seeded too
    }

    [Fact]
    public void HasEnvVar_ReturnsBool()
    {
        var env = new Dictionary<string, string> { ["set"] = "yes" };
        var r = _host.RunPreRequest(
            "bru.setVar('hasSet', bru.hasEnvVar('set').toString()); " +
            "bru.setVar('hasUnset', bru.hasEnvVar('unset').toString());",
            env);
        r.RuntimeVariables["hasSet"].Should().Be("true");
        r.RuntimeVariables["hasUnset"].Should().Be("false");
    }

    [Fact]
    public void SyntaxError_SurfacesAsFailure_DoesNotCrash()
    {
        var r = _host.RunPreRequest("this is { not ::: valid javascript", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ThrownError_SurfacesAsFailure()
    {
        var r = _host.RunPreRequest("throw new Error('boom');", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public void InfiniteLoop_TimesOutCleanly()
    {
        var host = new JintHost(timeout: TimeSpan.FromMilliseconds(200));
        var r = host.RunPreRequest("while (true) {}", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void NoFileSystemAccess_RequireUndefined()
    {
        // Jint by default does not expose require / fs. Confirm via reference error.
        var r = _host.RunPreRequest("var f = require('fs');", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cancellation_StopsScript()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var r = _host.RunPreRequest("while (true) {}", NoVars, cancellationToken: cts.Token);
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void MultipleSetVars_AllPersisted()
    {
        var script = """
            for (var i = 0; i < 5; i++) {
              bru.setVar('k' + i, 'v' + i);
            }
            """;
        var r = _host.RunPreRequest(script, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables.Should().HaveCount(5);
        r.RuntimeVariables["k0"].Should().Be("v0");
        r.RuntimeVariables["k4"].Should().Be("v4");
    }

    [Fact]
    public void SetEnvVar_AlsoVisibleViaGetVar_InSameRun()
    {
        var script = """
            bru.setEnvVar('newToken', 'fresh');
            bru.setVar('echo', bru.getVar('newToken'));
            """;
        var r = _host.RunPreRequest(script, NoVars);
        r.RuntimeVariables["echo"].Should().Be("fresh");
    }

    [Fact]
    public void Console_Log_CapturesLineWithLevel()
    {
        var script = """
            console.log('hello', 42);
            console.warn('careful');
            console.error('boom');
            """;
        var r = _host.RunPreRequest(script, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.ConsoleMessages.Should().HaveCount(3);
        r.ConsoleMessages[0].Level.Should().Be("log");
        r.ConsoleMessages[0].Text.Should().Contain("hello").And.Contain("42");
        r.ConsoleMessages[1].Level.Should().Be("warn");
        r.ConsoleMessages[2].Level.Should().Be("error");
    }

    [Fact]
    public void ScriptError_Surfaces_LineAndColumn()
    {
        var script = """
            // first line
            var a = 1;
            throw new Error('kaboom');
            """;
        var r = _host.RunPreRequest(script, NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("line 3");
    }

    [Fact]
    public void ToMatchSchema_PassesForValidJson()
    {
        // Use the post-response path so expect/test are wired.
        var resp = new ResponseApi(200, "OK", "{\"name\":\"alice\",\"age\":30}", 1,
            Array.Empty<KeyValuePair<string, string>>());
        var script = """
            test('matches', () => {
              const schema = { type: 'object', required: ['name','age'],
                properties: { name: {type:'string'}, age: {type:'number'} } };
              expect(res.body).toMatchSchema(schema);
            });
            """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes.Single().Passed.Should().BeTrue();
    }

    [Fact]
    public void ToMatchSchema_FailsForInvalidJson()
    {
        var resp = new ResponseApi(200, "OK", "{\"name\":\"alice\"}", 1,
            Array.Empty<KeyValuePair<string, string>>());
        var script = """
            test('mismatch', () => {
              const schema = { type: 'object', required: ['name','age'] };
              expect(res.body).toMatchSchema(schema);
            });
            """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.TestOutcomes.Single().Passed.Should().BeFalse();
    }
}
