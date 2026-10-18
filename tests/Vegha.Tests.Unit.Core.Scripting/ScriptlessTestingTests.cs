using FluentAssertions;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class ScriptlessTestingTests
{
    private const string Body = """
        {
          "access_token": "abc123",
          "expires_in": 3600,
          "user": { "id": 42, "email": "a@b.co", "roles": ["admin", "dev"] },
          "items": [
            { "id": 1, "status": "active", "price": 10 },
            { "id": 2, "status": "inactive", "price": 25 },
            { "id": 3, "status": "active", "price": 7 }
          ],
          "nothing": null,
          "odd-key": { "a b": true }
        }
        """;

    private static ResponseSnapshot Json(string body = Body, int status = 200) => new(
        status, "OK", body, 123,
        new[]
        {
            new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"),
            new KeyValuePair<string, string>("X-Request-Id", "req-9"),
            new KeyValuePair<string, string>("Set-Cookie", "session=s3cr3t; Path=/; HttpOnly"),
        });

    private static ResolvedValue Eval(string expr, ResponseSnapshot? r = null)
    {
        var result = ResponseValueResolver.Resolve(ResponseExpression.Parse(expr), r ?? Json());
        result.Error.Should().BeNull();
        return result.Value;
    }

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("res.status", ResponseSource.Status, "")]
    [InlineData("res.getStatus()", ResponseSource.Status, "")]
    [InlineData("res.responseTime", ResponseSource.ResponseTime, "")]
    [InlineData("res.body", ResponseSource.Body, "")]
    [InlineData("res.body.access_token", ResponseSource.Body, "access_token")]
    [InlineData("res.body.items[0].id", ResponseSource.Body, "items[0].id")]
    [InlineData("res.body[0]", ResponseSource.Body, "[0]")]
    [InlineData("res.headers['x-request-id']", ResponseSource.Header, "x-request-id")]
    [InlineData("res.headers[\"content-type\"]", ResponseSource.Header, "content-type")]
    [InlineData("res.getHeader('etag')", ResponseSource.Header, "etag")]
    [InlineData("res.cookies['session']", ResponseSource.Cookie, "session")]
    [InlineData("jsonpath('$..id')", ResponseSource.JsonPath, "$..id")]
    [InlineData("xpath('//a/text()')", ResponseSource.XPath, "//a/text()")]
    [InlineData("regex('id=(\\d+)')", ResponseSource.Regex, "id=(\\d+)")]
    [InlineData("'processing'", ResponseSource.Literal, "processing")]
    [InlineData("42", ResponseSource.Literal, "42")]
    [InlineData("res.body.items.find(i => i.id === 2).price", ResponseSource.Script, "res.body.items.find(i => i.id === 2).price")]
    public void Parse_recognizes_heads(string raw, ResponseSource source, string path)
    {
        var e = ResponseExpression.Parse(raw);
        e.Source.Should().Be(source);
        e.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("res.body.access_token")]
    [InlineData("res.headers['x-request-id']")]
    [InlineData("res.body.items | filter(status=active) | first | prop(\"id\")")]
    [InlineData("regex('id=(\\d+)', 2)")]
    [InlineData("res.status")]
    [InlineData("'processing'")]
    public void Storage_round_trips(string raw)
    {
        ResponseExpression.Parse(raw).ToStorage().Should().Be(raw);
    }

    [Fact]
    public void Logical_or_is_not_a_filter_pipeline()
    {
        var e = ResponseExpression.Parse("res.body.a || res.body.b");
        e.Source.Should().Be(ResponseSource.Script);
        e.Filters.Should().BeEmpty();
    }

    [Fact]
    public void Ui_model_renders_canonical_storage()
    {
        ResponseExpression.Create(ResponseSource.Header, "X-Id").ToStorage().Should().Be("res.headers['X-Id']");
        ResponseExpression.Create(ResponseSource.Body, "[0].id").ToStorage().Should().Be("res.body[0].id");
        ResponseExpression.Create(ResponseSource.Literal, "hello world").ToStorage().Should().Be("'hello world'");
    }

    // ------------------------------------------------------------------ resolving

    [Fact]
    public void Resolves_body_paths_headers_cookies_status()
    {
        Eval("res.body.access_token").ToVariableString().Should().Be("abc123");
        Eval("res.body.user.roles[1]").ToVariableString().Should().Be("dev");
        Eval("res.body.items[-1].id").ToVariableString().Should().Be("3");
        Eval("res.body.items[*].id").ToVariableString().Should().Be("[1,2,3]");
        Eval("res.body['odd-key']['a b']").ToVariableString().Should().Be("true");
        Eval("res.body.items.length").ToVariableString().Should().Be("3");
        Eval("res.headers['x-request-id']").ToVariableString().Should().Be("req-9");
        Eval("res.cookies['session']").ToVariableString().Should().Be("s3cr3t");
        Eval("res.status").ToVariableString().Should().Be("200");
        Eval("res.responseTime").ToVariableString().Should().Be("123");
    }

    [Fact]
    public void Missing_is_distinct_from_null()
    {
        Eval("res.body.nope").Exists.Should().BeFalse();
        Eval("res.body.nothing").IsNull.Should().BeTrue();
    }

    [Fact]
    public void Resolves_jsonpath_regex_and_xpath()
    {
        Eval("jsonpath('$.items[?@.status==\"active\"].id')").ToVariableString().Should().Be("[1,3]");
        Eval("jsonpath('$.user.id')").ToVariableString().Should().Be("42");
        Eval("regex('\"access_token\":\\s*\"(\\w+)\"')").ToVariableString().Should().Be("abc123");

        var xml = new ResponseSnapshot(200, "OK", "<r><token>t-1</token><n>1</n><n>2</n></r>", 5, null);
        Eval("xpath('/r/token')", xml).ToVariableString().Should().Be("t-1");
        Eval("xpath('count(/r/n)')", xml).ToVariableString().Should().Be("2");
    }

    [Fact]
    public void Filters_chain()
    {
        Eval("res.body.items | filter(status=active) | last | prop(\"id\")").ToVariableString().Should().Be("3");
        Eval("res.body.items | filter(price>=10) | count").ToVariableString().Should().Be("2");
        Eval("res.body.items | map(\"id\") | join(\"-\")").ToVariableString().Should().Be("1-2-3");
        Eval("res.body.user.email | upper").ToVariableString().Should().Be("A@B.CO");
        Eval("res.body.access_token | btoa | atob").ToVariableString().Should().Be("abc123");
        Eval("res.body.expires_in | add(60)").ToVariableString().Should().Be("3660");
        Eval("res.body.access_token | substring(-3)").ToVariableString().Should().Be("123");
        Eval("res.body.nope | default(\"x\")").ToVariableString().Should().Be("x");
        Eval("res.body.access_token | hash(\"SHA256\", \"hex\")").ToVariableString()
            .Should().Be("6ca13d52ca70c883e0f0bb101e425a89e8624de51db2d2392593af6a84118090");
    }

    [Fact]
    public void Filter_errors_are_reported_not_thrown()
    {
        var r = ResponseValueResolver.Resolve(ResponseExpression.Parse("res.body.user | first"), Json());
        r.IsError.Should().BeTrue();
        r.Error.Should().Contain("not an array");

        ResponseValueResolver.Resolve(ResponseExpression.Parse("res.body | nosuch"), Json())
            .Error.Should().Contain("Unknown filter");
    }

    [Fact]
    public void Script_fallback_uses_bruno_shaped_res()
    {
        var host = new JintHost();
        var r = ResponseValueResolver.Resolve(
            ResponseExpression.Parse("res.body.items.find(i => i.id === 2).price"),
            Json(),
            scriptEvaluator: (e, snap) => host.EvaluateResponseExpression(e, snap));
        r.Error.Should().BeNull();
        r.Value.ToVariableString().Should().Be("25");

        host.EvaluateResponseExpression("res.headers['x-request-id']", Json()).ToVariableString().Should().Be("req-9");
        host.EvaluateResponseExpression("res.body.nope", Json()).Exists.Should().BeFalse();
        var act = () => host.EvaluateResponseExpression("res.body.(", Json());
        act.Should().Throw<FilterException>();
    }

    // ------------------------------------------------------------------ assertions

    [Theory]
    [InlineData("res.status", "eq 200", true)]
    [InlineData("res.status", "200", true)] // no operator → eq
    [InlineData("res.status", "eq \"200\"", true)]
    [InlineData("res.status", "neq 200", false)]
    [InlineData("res.status", "in 200, 201", true)]
    [InlineData("res.status", "between 200, 299", true)]
    [InlineData("res.responseTime", "lt 1000", true)]
    [InlineData("res.responseTime", "gte 1000", false)]
    [InlineData("res.body.user.id", "isNumber", true)]
    [InlineData("res.body.user.id", "isString", false)]
    [InlineData("res.body.user.roles", "contains admin", true)]
    [InlineData("res.body.user.roles", "length 2", true)]
    [InlineData("res.body.user", "contains email", true)]
    [InlineData("res.body.user.email", "isEmail", true)]
    [InlineData("res.body.access_token", "matches ^[a-z]+\\d+$", true)]
    [InlineData("res.body.access_token", "startsWith abc", true)]
    [InlineData("res.body.nope", "isUndefined", true)]
    [InlineData("res.body.nope", "isDefined", false)]
    [InlineData("res.body.nothing", "isNull", true)]
    [InlineData("res.body.items", "isNotEmpty", true)]
    [InlineData("res.body.user", "isJson", true)]
    [InlineData("res.body.user.roles", "eq [\"admin\",\"dev\"]", true)]
    [InlineData("res.headers['content-type']", "contains json", true)]
    [InlineData("res.body.items | filter(status=active) | count", "eq 2", true)]
    [InlineData("res.body.user", "matchesSchema {\"type\":\"object\",\"required\":[\"id\",\"email\"]}", true)]
    [InlineData("res.body.user", "matchesSchema {\"type\":\"object\",\"required\":[\"phone\"]}", false)]
    public void Assertions(string lhs, string spec, bool expected)
    {
        var o = AssertionEvaluator.Evaluate(lhs, spec, Json());
        o.Passed.Should().Be(expected, o.FailureMessage);
        if (!expected) o.FailureMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Assertion_operand_is_interpolated_and_named()
    {
        var o = AssertionEvaluator.Evaluate("res.body.user.id", "eq {{uid}}", Json(),
            interpolate: s => s.Replace("{{uid}}", "41"));
        o.Passed.Should().BeFalse();
        o.Name.Should().Be("res.body.user.id eq {{uid}}");
        o.FailureMessage.Should().Be("expected 41, got 42");
    }

    [Fact]
    public void Assertion_spec_round_trips()
    {
        AssertionSpec.Parse("isNumber").Should().Be(new AssertionSpec("isNumber", ""));
        AssertionSpec.Parse("eq  200 ").ToStorage().Should().Be("eq 200");
        AssertionSpec.Parse("hello").Should().Be(new AssertionSpec("eq", "hello"));
    }

    // ------------------------------------------------------------------ captures

    [Fact]
    public void Captures_bucket_by_scope_and_honor_setIfValue()
    {
        var result = CaptureEvaluator.Evaluate(new[]
        {
            new CaptureRule("token", "res.body.access_token", CaptureScope.Runtime),
            new CaptureRule("reqId", "res.headers['x-request-id']", CaptureScope.Environment),
            new CaptureRule("uid", "res.body.user.id", CaptureScope.Global),
            new CaptureRule("skipped", "res.body.nope | setIfValue", CaptureScope.Runtime),
            new CaptureRule("emptied", "res.body.nope", CaptureScope.Runtime),
            new CaptureRule("disabled", "res.status", CaptureScope.Runtime, Enabled: false),
            new CaptureRule("broken", "res.body | first", CaptureScope.Runtime),
        }, Json());

        result.Runtime.Should().BeEquivalentTo(new Dictionary<string, string> { ["token"] = "abc123", ["emptied"] = "" });
        result.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["reqId"] = "req-9" });
        result.Global.Should().BeEquivalentTo(new Dictionary<string, string> { ["uid"] = "42" });
        result.Outcomes.Single(o => o.Name == "skipped").Applied.Should().BeFalse();
        result.Outcomes.Single(o => o.Name == "broken").Error.Should().NotBeNull();
        result.Outcomes.Should().NotContain(o => o.Name == "disabled");
        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Filter_condition_operators()
    {
        FilterCondition.Parse("a>=5").Should().Be(new FilterCondition("a", ">=", "5"));
        FilterCondition.Parse("name^=\"Jo\"").Should().Be(new FilterCondition("name", "^=", "Jo"));
        FilterCondition.Parse("x=1").Should().Be(new FilterCondition("x", "=", "1"));
        FilterCondition.Compare(ResolvedValue.Of("hello"), "*=", "ell").Should().BeTrue();
        FilterCondition.Compare(ResolvedValue.Of(10L), "<", "9").Should().BeFalse();
    }
}
