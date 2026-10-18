using FluentAssertions;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class ScriptlessToPostmanTests
{
    [Fact]
    public void Captures_map_to_scoped_setters()
    {
        var r = ScriptlessToPostman.Captures(new[]
        {
            new CaptureRule("token", "res.body.access_token", CaptureScope.Runtime),
            new CaptureRule("sid", "res.headers['x-session']", CaptureScope.Environment),
            new CaptureRule("uid", "res.body.items[-1].id", CaptureScope.Global),
            new CaptureRule("first", "res.body.items | first | prop(\"id\")", CaptureScope.Runtime),
            new CaptureRule("maybe", "res.body.refresh | setIfValue", CaptureScope.Runtime),
            new CaptureRule("ids", "res.body.items[*].id", CaptureScope.Runtime),
            new CaptureRule("jp", "jsonpath('$..id')", CaptureScope.Runtime),
            new CaptureRule("off", "res.status", CaptureScope.Runtime, Enabled: false),
        });

        r.Lines.Should().Contain("pm.variables.set(\"token\", pm.response.json()?.access_token);");
        r.Lines.Should().Contain("pm.environment.set(\"sid\", pm.response.headers.get(\"x-session\"));");
        r.Lines.Should().Contain("pm.globals.set(\"uid\", pm.response.json()?.items?.at(-1)?.id);");
        r.Lines.Should().Contain("pm.variables.set(\"ids\", (pm.response.json()?.items)?.map(e => e?.id));");
        r.Lines.Should().Contain(l => l.Contains("if (v !== undefined") && l.Contains("pm.variables.set(\"maybe\", v)"));
        r.Untranslated.Should().BeEquivalentTo("first = res.body.items | first | prop(\"id\")", "jp = jsonpath('$..id')");
        r.Lines.Should().Contain("// Vegha capture not translated: jp = jsonpath('$..id')");
        r.Lines.Should().NotContain(l => l.Contains("\"off\""));
    }

    [Fact]
    public void Assertions_map_to_pm_tests()
    {
        var r = ScriptlessToPostman.Assertions(new (string, string, bool)[]
        {
            ("res.status", "eq 200", true),
            ("res.body.name", "eq {{expectedName}}", true),
            ("res.responseTime", "lt 2000", true),
            ("res.body.roles", "contains admin", true),
            ("res.status", "in 200, 201", true),
            ("res.body.id", "isNumber", true),
            ("res.body.items | count", "between 1, 5", true),
            ("res.body", "matchesSchema {\"type\":\"object\"}", true),
        });

        r.Lines[0].Should().Be("pm.test(\"res.status eq 200\", function () { const actual = pm.response.code; pm.expect(Number(actual)).to.eql(200); });");
        r.Lines[1].Should().Contain("pm.expect(String(actual)).to.eql(pm.variables.replaceIn(\"{{expectedName}}\"))");
        r.Lines[2].Should().Contain("pm.expect(actual).to.be.below(2000)");
        r.Lines[3].Should().Contain("pm.expect(actual).to.include(\"admin\")");
        r.Lines[4].Should().Contain("to.be.oneOf([200, 201])");
        r.Lines[5].Should().Contain("to.be.a('number')");
        r.Lines[6].Should().Contain("const actual = (pm.response.json()?.items)?.length;").And.Contain("to.be.within(1, 5)");
        r.Untranslated.Should().ContainSingle().Which.Should().StartWith("res.body matchesSchema");
    }

    [Fact]
    public void Every_generated_line_is_valid_javascript()
    {
        var captures = ScriptlessToPostman.Captures(new[]
        {
            new CaptureRule("a", "res.body['odd key'][0] | upper", CaptureScope.Runtime),
            new CaptureRule("b", "regex('id=(\\d+)')", CaptureScope.Runtime),
            new CaptureRule("c", "'literal'", CaptureScope.Environment),
            new CaptureRule("d", "res.cookies['sid'] | default(\"none\") | setIfValue", CaptureScope.Runtime),
        });
        var assertions = ScriptlessToPostman.Assertions(
            AssertionOperators.All
                .Where(o => o.Token != "matchesSchema")
                .Select(o => ("res.body.x", o.Unary ? o.Token : $"{o.Token} {o.Hint ?? "1"}", true)));

        var script = string.Join("\n", captures.Lines.Concat(assertions.Lines));
        ScriptDiagnostics.Analyze(script).Should().BeEmpty(script);
        assertions.Untranslated.Should().BeEmpty();
    }
}
