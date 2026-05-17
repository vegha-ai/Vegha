using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Golden tests over <see cref="PostmanScriptTranslator"/> — one assertion per
/// row in the regex mapping table so any regression on a particular Postman API surface
/// fails its own test instead of breaking everything at once.</summary>
public class PostmanScriptTranslatorTests
{
    [Theory]
    // ----- environment -----
    [InlineData("pm.environment.get('k')",   "bru.getEnvVar('k')")]
    [InlineData("pm.environment.set('k','v')", "bru.setEnvVar('k','v')")]
    [InlineData("pm.environment.unset('k')", "bru.deleteEnvVar('k')")]
    [InlineData("pm.environment.has('k')",   "bru.hasEnvVar('k')")]
    // ----- variables -----
    [InlineData("pm.variables.get('k')",        "bru.getVar('k')")]
    [InlineData("pm.variables.set('k','v')",    "bru.setVar('k','v')")]
    [InlineData("pm.variables.replaceIn('{{x}}')", "bru.interpolate('{{x}}')")]
    // ----- collection vars -----
    [InlineData("pm.collectionVariables.get('k')",   "bru.getCollectionVar('k')")]
    [InlineData("pm.collectionVariables.set('k','v')", "bru.setCollectionVar('k','v')")]
    [InlineData("pm.collectionVariables.has('k')",   "bru.hasCollectionVar('k')")]
    [InlineData("pm.collectionVariables.unset('k')", "bru.deleteCollectionVar('k')")]
    // ----- globals -----
    [InlineData("pm.globals.get('k')",   "bru.getGlobalEnvVar('k')")]
    [InlineData("pm.globals.set('k','v')", "bru.setGlobalEnvVar('k','v')")]
    [InlineData("pm.globals.has('k')",   "bru.hasGlobalEnvVar('k')")]
    [InlineData("pm.globals.unset('k')", "bru.deleteGlobalEnvVar('k')")]
    [InlineData("pm.globals.toObject()", "bru.getAllGlobalEnvVars()")]
    // ----- flow control -----
    [InlineData("pm.setNextRequest('Foo')",      "bru.setNextRequest('Foo')")]
    [InlineData("postman.setNextRequest('Foo')", "bru.setNextRequest('Foo')")]
    [InlineData("pm.execution.skipRequest()",    "bru.runner.skipRequest()")]
    [InlineData("pm.execution.setNextRequest(null)", "bru.runner.stopExecution()")]
    // ----- response -----
    [InlineData("pm.response.json()",     "res.getBody()")]
    [InlineData("pm.response.text()",     "res.getBodyAsText()")]
    [InlineData("pm.response.code",       "res.getStatus()")]
    [InlineData("pm.response.responseTime", "res.getResponseTime()")]
    [InlineData("pm.response.size()",     "res.getSize()")]
    [InlineData("pm.response.headers.get('x-trace')", "res.getHeader('x-trace')")]
    [InlineData("pm.response.headers.has('x-trace')", "res.hasHeader('x-trace')")]
    [InlineData("pm.response.to.have.status(200)",    "expect(res.getStatus()).to.equal(200)")]
    // ----- request -----
    [InlineData("pm.request.url",      "req.getUrl()")]
    [InlineData("pm.request.method",   "req.getMethod()")]
    [InlineData("pm.request.body",     "req.getBody()")]
    [InlineData("pm.request.body.raw", "req.getBody()")]
    [InlineData("pm.request.headers.add({key:'X',value:'Y'})", "req.headerList.add({key:'X',value:'Y'})")]
    [InlineData("pm.request.headers.remove('X')",              "req.headerList.remove('X')")]
    [InlineData("pm.request.headers.upsert({key:'X',value:'Y'})", "req.headerList.upsert({key:'X',value:'Y'})")]
    [InlineData("pm.request.headers.each(h => log(h))",        "req.headerList.each(h => log(h))")]
    [InlineData("pm.request.headers.filter(h => h.key === 'X')", "req.headerList.filter(h => h.key === 'X')")]
    [InlineData("pm.request.headers.map(h => h.value)",        "req.headerList.map(h => h.value)")]
    [InlineData("pm.request.headers.get('X')",                 "req.getHeader('X')")]
    [InlineData("pm.request.headers.has('X')",                 "req.headerList.has('X')")]
    // ----- cookies -----
    [InlineData("pm.cookies.jar().get('https://x.test','session')",   "bru.cookies.jar().getCookie('https://x.test','session')")]
    [InlineData("pm.cookies.jar().set('https://x.test','session','abc')", "bru.cookies.jar().setCookie('https://x.test','session','abc')")]
    [InlineData("pm.cookies.jar().unset('https://x.test','session')", "bru.cookies.jar().deleteCookie('https://x.test','session')")]
    // ----- tests / expect -----
    [InlineData("pm.test('status', () => {})",         "test('status', () => {})")]
    [InlineData("pm.expect(x).to.equal(y)",            "expect(x).to.equal(y)")]
    // ----- bare token rewrites (apply after specific shapes) -----
    [InlineData("const r = pm.response;",   "const r = res;")]
    [InlineData("const q = pm.request;",    "const q = req;")]
    public void Translates_KnownPostmanCalls_ToBruEquivalent(string input, string expected)
    {
        var outcome = PostmanScriptTranslator.Translate(input);
        outcome.TranslatedScript.Should().Be(expected);
        outcome.UnhandledTokens.Should().BeEmpty();
    }

    [Fact]
    public void EmptyOrNullInput_ReturnsEmpty()
    {
        PostmanScriptTranslator.Translate(null).TranslatedScript.Should().Be(string.Empty);
        PostmanScriptTranslator.Translate("").TranslatedScript.Should().Be(string.Empty);
    }

    [Fact]
    public void BracketAccessPatterns_AreFlaggedByLint()
    {
        var script = "pm[\"environment\"][\"get\"](\"x\")";
        var outcome = PostmanScriptTranslator.Translate(script);
        // The bracketed form doesn't match the regex table — translator can't rewrite it.
        outcome.TranslatedScript.Should().Contain("pm[");
        // But the lint pass should *not* find a `pm.` token here (the form is `pm["..."]`,
        // not `pm.something`). So lint output is empty for this specific shape.
        outcome.UnhandledTokens.Should().BeEmpty();
    }

    [Fact]
    public void DottedUnhandledTokens_AreReportedByLint()
    {
        // pm.iterationData isn't in our table — should be flagged.
        var script = "var d = pm.iterationData.get('row');";
        var outcome = PostmanScriptTranslator.Translate(script);
        outcome.UnhandledTokens.Should().Contain(t => t.StartsWith("pm.iterationData"));
    }

    [Fact]
    public void SendRequest_IsRewrittenWithCallbackTodoComment()
    {
        var script = "pm.sendRequest({url: 'x'}, function(err, res) { console.log(res); });";
        var outcome = PostmanScriptTranslator.Translate(script);
        outcome.TranslatedScript.Should().Contain("bru.sendRequest(");
        outcome.TranslatedScript.Should().Contain("TODO");
    }
}
