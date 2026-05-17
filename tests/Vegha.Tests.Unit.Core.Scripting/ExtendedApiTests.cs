using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

/// <summary>Smoke tests over the new APIs added for Bruno parity: response getters,
/// request URL projections, header PropertyList, bru.interpolate, bru.sleep, bru.utils.</summary>
public class ExtendedApiTests
{
    private readonly JintHost _host = new();
    private static readonly Dictionary<string, string> NoVars = new();

    [Fact]
    public void Response_JsonBody_IsParsedWhenContentTypeMatches()
    {
        var resp = new ResponseApi(200, "OK", "{\"name\":\"alice\",\"age\":30}", 12,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") });
        var script = """
            test('parsed', () => {
                const b = res.getBody();
                expect(b.name).to.equal('alice');
                expect(b.age).to.equal(30);
            });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void Response_GetBodyAsText_AlwaysReturnsRawString()
    {
        var resp = new ResponseApi(200, "OK", "{\"x\":1}", 0,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") });
        var script = """
            test('raw', () => {
                expect(res.getBodyAsText()).to.equal('{"x":1}');
            });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void Response_GetSize_ReturnsHeaderBodyTotal()
    {
        var resp = new ResponseApi(200, "OK", "hello", 0,
            new[] { new KeyValuePair<string, string>("X-One", "abc") });
        var script = """
            test('size', () => {
                const s = res.getSize();
                expect(s.body).to.equal(5);
                expect(s.total).to.be.above(5);
            });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void Request_HeaderList_AddDuringPreRequest_IsVisibleToScript()
    {
        var req = new RequestApi("GET", "https://x.test/users/42", null,
            new[] { new KeyValuePair<string, string>("Accept", "application/json") },
            name: "Get user", pathParams: new[] { new KeyValuePair<string, string>("id", "42") });
        var script = """
            req.headerList.add({key: 'X-Trace', value: 'abc'});
            bru.setVar('hasHeader', req.headerList.has('X-Trace') ? 'yes' : 'no');
        """;
        var r = _host.RunPreRequest(script, NoVars, request: req);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables["hasHeader"].Should().Be("yes");
        req.Headers.Should().ContainKey("X-Trace");
    }

    [Fact]
    public void Request_UrlProjections_AreAvailable()
    {
        var req = new RequestApi("POST", "https://api.example.com:443/v1/users?role=admin",
            null, Array.Empty<KeyValuePair<string, string>>(), name: "Create user");
        var script = """
            bru.setVar('host', req.getHost());
            bru.setVar('path', req.getPath());
            bru.setVar('q', req.getQueryString());
            bru.setVar('n', req.getName());
        """;
        var r = _host.RunPreRequest(script, NoVars, request: req);
        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables["host"].Should().Be("api.example.com");
        r.RuntimeVariables["path"].Should().Be("/v1/users");
        r.RuntimeVariables["q"].Should().Be("role=admin");
        r.RuntimeVariables["n"].Should().Be("Create user");
    }

    [Fact]
    public void Bru_Interpolate_ResolvesAgainstCurrentVars()
    {
        var script = """
            bru.setVar('base', 'https://x.test');
            bru.setVar('full', bru.interpolate('{{base}}/users/42'));
        """;
        var r = _host.RunPreRequest(script, NoVars);
        r.RuntimeVariables["full"].Should().Be("https://x.test/users/42");
    }

    [Fact]
    public void Bru_Interpolate_HandlesDynamicVars()
    {
        var script = """
            bru.setVar('uuid', bru.interpolate('{{$randomUUID}}'));
        """;
        var r = _host.RunPreRequest(script, NoVars);
        Guid.TryParse(r.RuntimeVariables["uuid"], out _).Should().BeTrue();
    }

    [Fact]
    public void Bru_Utils_MinifyJson_StripsWhitespace()
    {
        var script = """
            bru.setVar('out', bru.utils.minifyJson('{\n  "a":  1\n}'));
        """;
        var r = _host.RunPreRequest(script, NoVars);
        r.RuntimeVariables["out"].Should().Be("{\"a\":1}");
    }

    [Fact]
    public void Bru_Sleep_DelaysWithinTimeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = _host.RunPreRequest("bru.sleep(80);", NoVars);
        sw.Stop();
        r.IsSuccess.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Bru_GlobalEnvVars_AliasToEnvVars()
    {
        var script = """
            bru.setGlobalEnvVar('region', 'us-east');
            bru.setVar('mirror', bru.getGlobalEnvVar('region'));
            bru.setVar('viaEnv', bru.getEnvVar('region'));
        """;
        var r = _host.RunPreRequest(script, NoVars);
        r.RuntimeVariables["mirror"].Should().Be("us-east");
        r.RuntimeVariables["viaEnv"].Should().Be("us-east");
        r.EnvVarMutations["region"].Should().Be("us-east");
    }

    [Fact]
    public void Cookies_Jar_ThrowsWhenNoJarSupplied()
    {
        // No cookie jar wired — bru.cookies.jar() should throw a clear error rather than NPE.
        var script = "bru.cookies.jar().getCookie('https://x.test', 'session');";
        var r = _host.RunPreRequest(script, NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("cookies");
    }
}
