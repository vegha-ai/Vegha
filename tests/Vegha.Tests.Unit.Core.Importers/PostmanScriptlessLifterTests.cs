using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class PostmanScriptlessLifterTests
{
    [Fact]
    public void Lifts_common_setters_and_single_check_tests()
    {
        var script = """
            var jsonData = pm.response.json();
            pm.environment.set("token", jsonData.access_token);
            pm.globals.set('uid', pm.response.json().user.id);
            pm.collectionVariables.set("last", pm.response.json().items.at(-1).name);
            pm.variables.set("rid", pm.response.headers.get("X-Request-Id"));

            pm.test("Status code is 200", function () {
                pm.response.to.have.status(200);
            });
            pm.test("fast", () => { pm.expect(pm.response.responseTime).to.be.below(500); });
            pm.test("name", function () { pm.expect(jsonData.user.name).to.eql("Ada"); });
            pm.test("roles", function () { pm.expect(pm.response.json().roles).to.include("admin"); });
            pm.test("id type", function () { pm.expect(jsonData.user.id).to.be.a('number'); });
            """;

        var r = PostmanScriptlessLifter.Lift(script);

        r.EnvCaptures.Should().Equal(new KvPair("token", "res.body.access_token"));
        r.GlobalCaptures.Should().Equal(new KvPair("uid", "res.body.user.id"));
        r.RuntimeCaptures.Should().Equal(
            new KvPair("last", "res.body.items[-1].name"),
            new KvPair("rid", "res.headers['X-Request-Id']"));
        r.Assertions.Should().Equal(
            new KvPair("res.status", "eq 200"),
            new KvPair("res.responseTime", "lt 500"),
            new KvPair("res.body.user.name", "eq Ada"),
            new KvPair("res.body.roles", "contains admin"),
            new KvPair("res.body.user.id", "isNumber"));
        r.RemainingScript.Should().BeNull("everything was lifted, including the unused jsonData alias");
    }

    [Fact]
    public void Leaves_anything_complex_as_javascript()
    {
        var script = """
            pm.environment.set("sum", pm.response.json().a + 1);
            pm.test("two checks", function () {
                pm.response.to.have.status(200);
                pm.expect(pm.response.json().ok).to.be.true;
            });
            pm.test("loop", function () { pm.response.json().items.forEach(i => pm.expect(i.id).to.be.ok); });
            """;
        var r = PostmanScriptlessLifter.Lift(script);
        r.LiftedAnything.Should().BeFalse();
        r.RemainingScript.Should().Be(script.Trim());
    }

    [Fact]
    public void Keeps_the_json_alias_when_remaining_code_uses_it()
    {
        var script = """
            var jsonData = pm.response.json();
            pm.environment.set("token", jsonData.token);
            console.log(jsonData.debug);
            """;
        var r = PostmanScriptlessLifter.Lift(script);
        r.EnvCaptures.Should().ContainSingle();
        r.RemainingScript.Should().Contain("var jsonData = pm.response.json();").And.Contain("console.log(jsonData.debug);");
    }
}
