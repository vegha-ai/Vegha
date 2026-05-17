using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class JsPreloadsTests
{
    private readonly JintHost _host = new(subRequestExecutor: new FakeBruExecutor());
    private static readonly Dictionary<string, string> NoVars = new();

    [Fact]
    public void LodashGet_NestedPath_ReturnsValue()
    {
        var r = _host.RunPreRequest(
            """
            var obj = { a: { b: { c: 42 } } };
            bru.setVar('val', String(_.get(obj, 'a.b.c')));
            bru.setVar('def', _.get(obj, 'a.b.x', 'fallback'));
            """, NoVars);

        r.RuntimeVariables["val"].Should().Be("42");
        r.RuntimeVariables["def"].Should().Be("fallback");
    }

    [Fact]
    public void LodashSet_AndCloneDeep_Work()
    {
        var r = _host.RunPreRequest(
            """
            var obj = {};
            _.set(obj, 'a.b.c', 'hi');
            var clone = _.cloneDeep(obj);
            clone.a.b.c = 'mutated';
            bru.setVar('orig', obj.a.b.c);
            bru.setVar('clone', clone.a.b.c);
            """, NoVars);

        r.RuntimeVariables["orig"].Should().Be("hi");
        r.RuntimeVariables["clone"].Should().Be("mutated");
    }

    [Fact]
    public void LodashIsEmpty_HandlesArrayObjectAndString()
    {
        var r = _host.RunPreRequest(
            """
            bru.setVar('emptyArr', String(_.isEmpty([])));
            bru.setVar('nonemptyArr', String(_.isEmpty([1])));
            bru.setVar('emptyObj', String(_.isEmpty({})));
            bru.setVar('nonemptyObj', String(_.isEmpty({a:1})));
            bru.setVar('emptyStr', String(_.isEmpty('')));
            bru.setVar('nullVal', String(_.isEmpty(null)));
            """, NoVars);

        r.RuntimeVariables["emptyArr"].Should().Be("true");
        r.RuntimeVariables["nonemptyArr"].Should().Be("false");
        r.RuntimeVariables["emptyObj"].Should().Be("true");
        r.RuntimeVariables["nonemptyObj"].Should().Be("false");
        r.RuntimeVariables["emptyStr"].Should().Be("true");
        r.RuntimeVariables["nullVal"].Should().Be("true");
    }

    [Fact]
    public void Axios_Get_RoutesThroughBruSendRequest()
    {
        var r = _host.RunPreRequest(
            """
            var r = axios.get('https://api.test/health');
            bru.setVar('status', String(r.status));
            bru.setVar('field', r.data.ok ? 'true' : 'false');
            """, NoVars);

        r.RuntimeVariables["status"].Should().Be("200");
        r.RuntimeVariables["field"].Should().Be("true");
    }

    [Fact]
    public void LodashPickAndOmit_KeyFiltering()
    {
        var r = _host.RunPreRequest(
            """
            var src = { a: 1, b: 2, c: 3 };
            var picked = _.pick(src, ['a', 'c']);
            var omitted = _.omit(src, ['b']);
            bru.setVar('pickedKeys', Object.keys(picked).join(','));
            bru.setVar('omittedKeys', Object.keys(omitted).sort().join(','));
            """, NoVars);

        r.RuntimeVariables["pickedKeys"].Should().Be("a,c");
        r.RuntimeVariables["omittedKeys"].Should().Be("a,c");
    }

    private sealed class FakeBruExecutor : IBruRequestExecutor
    {
        public BruRequestResult Send(BruRequestOptions options) =>
            new(200, "OK", "{\"ok\":true}", 5,
                new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
                Error: null);
    }
}
