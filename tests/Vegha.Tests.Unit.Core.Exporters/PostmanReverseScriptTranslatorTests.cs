using FluentAssertions;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Exporters;

/// <summary>Golden tests over <see cref="PostmanScriptTranslator.TranslateToPostman"/> — the
/// export-direction (bru → pm) table. One row per mapping, plus round-trips through the
/// import-direction table.</summary>
public class PostmanReverseScriptTranslatorTests
{
    [Theory]
    [InlineData("bru.getEnvVar('k')", "pm.environment.get('k')")]
    [InlineData("bru.setEnvVar('k','v')", "pm.environment.set('k','v')")]
    [InlineData("bru.deleteEnvVar('k')", "pm.environment.unset('k')")]
    [InlineData("bru.getEnvName()", "pm.environment.name")]
    [InlineData("bru.getVar('k')", "pm.variables.get('k')")]
    [InlineData("bru.setVar('k', 1)", "pm.variables.set('k', 1)")]
    [InlineData("bru.interpolate('{{x}}')", "pm.variables.replaceIn('{{x}}')")]
    [InlineData("bru.getCollectionVar('k')", "pm.collectionVariables.get('k')")]
    [InlineData("bru.getGlobalEnvVar('k')", "pm.globals.get('k')")]
    [InlineData("bru.setNextRequest('Foo')", "pm.execution.setNextRequest('Foo')")]
    [InlineData("bru.runner.skipRequest()", "pm.execution.skipRequest()")]
    [InlineData("bru.runner.stopExecution()", "pm.execution.setNextRequest(null)")]
    [InlineData("expect(res.getStatus()).to.equal(201)", "pm.response.to.have.status(201)")]
    [InlineData("res.getBody().id", "pm.response.json().id")]
    [InlineData("JSON.parse(res.body)", "JSON.parse(pm.response.text())")]
    [InlineData("res.responseTime", "pm.response.responseTime")]
    [InlineData("res.getStatusText()", "pm.response.status")]
    [InlineData("res.getBodyAsText()", "pm.response.text()")]
    [InlineData("res.getStatus()", "pm.response.code")]
    [InlineData("res.status", "pm.response.code")]
    [InlineData("res.getResponseTime()", "pm.response.responseTime")]
    [InlineData("res.getHeader('x')", "pm.response.headers.get('x')")]
    [InlineData("req.getUrl()", "pm.request.url.toString()")]
    [InlineData("req.getMethod()", "pm.request.method")]
    [InlineData("req.getHeader('x')", "pm.request.headers.get('x')")]
    [InlineData("bru.cookies.jar().getCookie('u', 'n')", "pm.cookies.jar().get('u', 'n')")]
    [InlineData("test('t', () => {})", "pm.test('t', () => {})")]
    [InlineData("expect(1).to.equal(1)", "pm.expect(1).to.equal(1)")]
    public void Maps_bru_api_to_pm(string input, string expected)
    {
        var outcome = PostmanScriptTranslator.TranslateToPostman(input);
        outcome.TranslatedScript.Should().Be(expected);
        outcome.UnhandledTokens.Should().BeEmpty();
    }

    [Theory]
    [InlineData("function test(x) { return x; }")]
    [InlineData("myres.getBody()")]
    [InlineData("obj.test('a')")]
    [InlineData("const expected = 1;")]
    public void Leaves_lookalikes_alone(string input) =>
        PostmanScriptTranslator.TranslateToPostman(input).TranslatedScript.Should().Be(input);

    [Fact]
    public void Reports_unmapped_tokens()
    {
        var outcome = PostmanScriptTranslator.TranslateToPostman("req.setHeader('a','b'); bru.sleep(10);");
        outcome.UnhandledTokens.Should().BeEquivalentTo("req.setHeader", "bru.sleep");
    }

    [Theory]
    [InlineData("bru.setEnvVar('token', res.getBody().token);")]
    [InlineData("test('ok', () => expect(res.getStatus()).to.equal(200));")]
    [InlineData("const v = bru.getVar('a'); bru.setCollectionVar('b', v);")]
    public void Round_trips_through_the_import_table(string script)
    {
        var exported = PostmanScriptTranslator.TranslateToPostman(script).TranslatedScript;
        PostmanScriptTranslator.Translate(exported).TranslatedScript.Should().Be(script);
    }

    [Fact]
    public void Empty_input_is_empty() =>
        PostmanScriptTranslator.TranslateToPostman(null).TranslatedScript.Should().BeEmpty();
}
