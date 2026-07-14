using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

/// <summary>
/// Covers the Postman-compatible <c>require(...)</c> registry and injected globals
/// (<c>xml2Json</c> / <c>atob</c> / <c>btoa</c>) added in <see cref="JsModules"/> +
/// <see cref="ScriptModuleHost"/>. These are the libraries ported Postman scripts assume
/// are present; the suite guards the shapes those scripts actually depend on.
/// </summary>
public class RequireModulesTests
{
    private readonly JintHost _host = new(subRequestExecutor: new FakeExecutor());
    private static readonly Dictionary<string, string> NoVars = new();

    private string Run(string script)
    {
        var r = _host.RunPreRequest(script, NoVars);
        r.IsSuccess.Should().BeTrue(r.ErrorMessage);
        return r.RuntimeVariables.TryGetValue("out", out var v) ? v : "";
    }

    // ---- xml2Json global ----

    [Fact]
    public void Xml2Json_LeafText_CollapsesToString()
    {
        Run("bru.setVar('out', xml2Json('<note>hi</note>').note);")
            .Should().Be("hi");
    }

    [Fact]
    public void Xml2Json_RepeatedChildren_BecomeArray()
    {
        Run("""
            var j = xml2Json('<list><item>a</item><item>b</item></list>');
            bru.setVar('out', j.list.item.join(','));
            """).Should().Be("a,b");
    }

    [Fact]
    public void Xml2Json_Attributes_UnderDollar_TextUnderUnderscore()
    {
        Run("""
            var j = xml2Json('<root><node id="7">val</node></root>');
            bru.setVar('out', j.root.node[0].$.id + '/' + j.root.node[0]._);
            """).Should().Be("7/val");
    }

    [Fact]
    public void Xml2js_ParseString_SyncCallback_Works()
    {
        Run("""
            var xml2js = require('xml2js');
            var got;
            xml2js.parseString('<a><b>x</b></a>', function (err, res) { got = res.a.b[0]; });
            bru.setVar('out', got);
            """).Should().Be("x");
    }

    // ---- crypto-js ----

    [Fact]
    public void CryptoJs_Sha256_Hex_And_Base64()
    {
        // SHA256("abc") known digest.
        Run("""
            var C = require('crypto-js');
            bru.setVar('out', C.SHA256('abc').toString());
            """).Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        Run("""
            var C = require('crypto-js');
            bru.setVar('out', C.SHA256('abc').toString(C.enc.Base64));
            """).Should().Be("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=");
    }

    [Fact]
    public void CryptoJs_HmacSha256_MatchesKnownVector()
    {
        // HMAC-SHA256(key="key", msg="The quick brown fox jumps over the lazy dog")
        Run("""
            var C = require('crypto-js');
            bru.setVar('out', C.HmacSHA256('The quick brown fox jumps over the lazy dog', 'key').toString());
            """).Should().Be("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");
    }

    [Fact]
    public void CryptoJs_Md5_KnownVector()
    {
        Run("""
            var C = require('crypto-js');
            bru.setVar('out', C.MD5('abc').toString());
            """).Should().Be("900150983cd24fb0d6963f7d28e17f72");
    }

    // ---- uuid ----

    [Fact]
    public void Uuid_V4_ProducesValidUuid()
    {
        var v = Run("""
            var uuid = require('uuid');
            bru.setVar('out', uuid.v4());
            """);
        System.Guid.TryParse(v, out _).Should().BeTrue();
    }

    [Fact]
    public void Uuid_Validate_Works()
    {
        Run("""
            var uuid = require('uuid');
            bru.setVar('out', String(uuid.validate('not-a-uuid')));
            """).Should().Be("false");
    }

    // ---- atob / btoa (globals + modules) ----

    [Fact]
    public void Btoa_Atob_RoundTrip_AsGlobals()
    {
        Run("""
            var enc = btoa('hello');
            bru.setVar('out', enc + '|' + atob(enc));
            """).Should().Be("aGVsbG8=|hello");
    }

    [Fact]
    public void Atob_AsModule()
    {
        Run("""
            var atobFn = require('atob');
            bru.setVar('out', atobFn('d29ybGQ='));
            """).Should().Be("world");
    }

    // ---- lodash (extended) ----

    [Fact]
    public void Lodash_GroupBy_And_Uniq_ViaRequire()
    {
        Run("""
            var _l = require('lodash');
            var g = _l.groupBy([6.1, 4.2, 6.3], Math.floor);
            bru.setVar('out', Object.keys(g).sort().join(',') + '|' + _l.uniq([1,1,2,3,3]).join(','));
            """).Should().Be("4,6|1,2,3");
    }

    // ---- moment (light) ----

    [Fact]
    public void Moment_Format_And_Add()
    {
        Run("""
            var moment = require('moment');
            var m = moment('2020-01-15T00:00:00.000Z').add(1, 'months');
            bru.setVar('out', m.format('YYYY-MM'));
            """).Should().Be("2020-02");
    }

    [Fact]
    public void Moment_Diff_InDays()
    {
        Run("""
            var moment = require('moment');
            var a = moment('2020-01-10T00:00:00.000Z');
            var b = moment('2020-01-01T00:00:00.000Z');
            bru.setVar('out', String(a.diff(b, 'days')));
            """).Should().Be("9");
    }

    // ---- chai ----

    [Fact]
    public void Chai_Expect_Passes_And_Fails()
    {
        Run("""
            var chai = require('chai');
            chai.expect(1 + 1).to.equal(2);
            chai.expect([1,2,3]).to.include(2);
            bru.setVar('out', 'ok');
            """).Should().Be("ok");

        var r = _host.RunPreRequest("""
            var chai = require('chai');
            chai.expect(1).to.equal(2);
            """, NoVars);
        r.IsSuccess.Should().BeFalse();
    }

    // ---- tv4 / ajv schema validation ----

    [Fact]
    public void Tv4_Validate_TypeAndRequired()
    {
        Run("""
            var tv4 = require('tv4');
            var schema = { type: 'object', required: ['name'], properties: { name: { type: 'string' } } };
            var ok = tv4.validate({ name: 'x' }, schema);
            var bad = tv4.validate({ }, schema);
            bru.setVar('out', ok + '/' + bad);
            """).Should().Be("true/false");
    }

    [Fact]
    public void Ajv_Compile_Validates()
    {
        Run("""
            var Ajv = require('ajv');
            var ajv = new Ajv();
            var validate = ajv.compile({ type: 'object', properties: { n: { type: 'number' } }, required: ['n'] });
            bru.setVar('out', validate({ n: 5 }) + '/' + validate({ n: 'no' }));
            """).Should().Be("true/false");
    }

    // ---- csv-parse/lib/sync ----

    [Fact]
    public void CsvParse_Sync_WithColumns()
    {
        Run("""
            var parse = require('csv-parse/lib/sync');
            var recs = parse('a,b\n1,2\n3,4', { columns: true });
            bru.setVar('out', recs.length + '|' + recs[0].a + recs[0].b + '|' + recs[1].a + recs[1].b);
            """).Should().Be("2|12|34");
    }

    [Fact]
    public void CsvParse_HandlesQuotedFields()
    {
        Run("""
            var parse = require('csv-parse/lib/sync');
            var rows = parse('"x,y",z\n"a""b",c');
            bru.setVar('out', rows[0][0] + '|' + rows[1][0]);
            """).Should().Be("x,y|a\"b");
    }

    // ---- Node core: querystring / url / path ----

    [Fact]
    public void Querystring_ParseAndStringify()
    {
        Run("""
            var qs = require('querystring');
            var p = qs.parse('a=1&b=hello%20world');
            bru.setVar('out', p.a + '|' + p.b + '|' + qs.stringify({ x: 1, y: 'a b' }));
            """).Should().Be("1|hello world|x=1&y=a%20b");
    }

    [Fact]
    public void Url_Parse_ExtractsParts()
    {
        Run("""
            var url = require('url');
            var u = url.parse('https://user@example.com:8080/a/b?q=1#frag');
            bru.setVar('out', u.protocol + '//' + u.hostname + ':' + u.port + u.pathname + u.search);
            """).Should().Be("https://example.com:8080/a/b?q=1");
    }

    [Fact]
    public void Path_BasenameDirnameExtname()
    {
        Run("""
            var path = require('path');
            bru.setVar('out', path.basename('/a/b/c.txt') + '|' + path.dirname('/a/b/c.txt') + '|' + path.extname('/a/b/c.txt'));
            """).Should().Be("c.txt|/a/b|.txt");
    }

    // ---- Node core: buffer (global + module) ----

    [Fact]
    public void Buffer_Base64_And_Hex_RoundTrip()
    {
        Run("""
            bru.setVar('out', Buffer.from('hello').toString('base64') + '|' + Buffer.from('aGVsbG8=', 'base64').toString('utf8'));
            """).Should().Be("aGVsbG8=|hello");

        Run("""
            bru.setVar('out', Buffer.from('AB', 'utf8').toString('hex'));
            """).Should().Be("4142");
    }

    // ---- unknown / unsupported modules ----

    [Fact]
    public void Require_UnknownModule_ThrowsListingError()
    {
        var r = _host.RunPreRequest("require('totally-made-up');", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("Cannot find module");
        r.ErrorMessage.Should().Contain("lodash");
    }

    [Fact]
    public void Require_UnsupportedHeavyModule_ThrowsClearError()
    {
        var r = _host.RunPreRequest("require('postman-collection');", NoVars);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("not available in the Vegha sandbox");
    }

    private sealed class FakeExecutor : IBruRequestExecutor
    {
        public BruRequestResult Send(BruRequestOptions options) =>
            new(200, "OK", "{\"ok\":true}", 5,
                new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
                Error: null);
    }
}
