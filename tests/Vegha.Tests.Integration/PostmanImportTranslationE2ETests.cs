using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>End-to-end: import a Postman v2.1 fixture that uses <c>pm.environment.set</c>,
/// <c>pm.test</c>, <c>pm.expect(...).to.equal(...)</c>, and <c>pm.response.json()</c>, then
/// run the imported scripts through <see cref="RequestComposition"/> + <see cref="JintHost"/>.
/// Confirms that with translation enabled the scripts actually run; with it disabled they
/// fail cleanly with <c>pm is not defined</c>.</summary>
public class PostmanImportTranslationE2ETests
{
    private const string FixturePostmanJson = """
    {
      "info": { "name": "Fixture", "_postman_id": "x" },
      "item": [{
        "name": "Get user",
        "request": { "method": "GET", "url": "https://x.test/users/42" },
        "event": [
          {
            "listen": "prerequest",
            "script": {
              "exec": [
                "pm.environment.set('userId', '42');",
                "pm.variables.set('trace', 'pre');"
              ]
            }
          },
          {
            "listen": "test",
            "script": {
              "exec": [
                "pm.test('parsed', function () {",
                "  var b = pm.response.json();",
                "  pm.expect(b.id).to.equal(42);",
                "});",
                "pm.test('status', function () {",
                "  pm.response.to.have.status(200);",
                "});"
              ]
            }
          }
        ]
      }]
    }
    """;

    [Fact]
    public void WithTranslationOn_ImportedScriptsExecuteAgainstBruApi()
    {
        var col = PostmanV2Importer.ImportFromJson(FixturePostmanJson, new PostmanImportOptions(TranslateScripts: true));
        var req = col.Requests.Single();
        req.PreRequestScript.Should().Contain("bru.setEnvVar");
        req.Tests.Should().Contain("test('parsed'");
        req.Tests.Should().Contain("res.getBody()");
        req.Tests.Should().Contain("expect(res.getStatus()).to.equal(200)");

        // Compose (no parent layers) and run.
        var composed = RequestComposition.Compose(col, Array.Empty<Folder>(), req);
        composed.PreRequestScript.Should().Be(req.PreRequestScript);

        var host = new JintHost();
        var pre = host.RunPreRequest(composed.PreRequestScript!, new Dictionary<string, string>());
        pre.IsSuccess.Should().BeTrue();
        pre.EnvVarMutations["userId"].Should().Be("42");
        pre.RuntimeVariables["trace"].Should().Be("pre");

        var resp = new ResponseApi(200, "OK", "{\"id\":42,\"name\":\"alice\"}", 5,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") });
        var post = host.RunPostResponse(null, composed.TestsScript, resp, new Dictionary<string, string>());
        post.IsSuccess.Should().BeTrue();
        post.TestOutcomes.Should().HaveCount(2);
        post.TestOutcomes.Should().AllSatisfy(t => t.Passed.Should().BeTrue());
    }

    [Fact]
    public void WithTranslationOff_ImportedScriptsFailWithPmIsNotDefined()
    {
        var col = PostmanV2Importer.ImportFromJson(FixturePostmanJson, new PostmanImportOptions(TranslateScripts: false));
        var req = col.Requests.Single();
        req.PreRequestScript.Should().Contain("pm.environment.set");

        var host = new JintHost();
        var pre = host.RunPreRequest(req.PreRequestScript!, new Dictionary<string, string>());
        pre.IsSuccess.Should().BeFalse();
        pre.ErrorMessage.Should().Contain("pm");
    }

    [Fact]
    public void Translation_Diagnostics_FireForUnhandledPatterns()
    {
        var json = """
        {
          "info": { "name": "Fixture" },
          "item": [{
            "name": "weird",
            "request": { "method": "GET", "url": "https://x.test" },
            "event": [{
              "listen": "prerequest",
              "script": { "exec": ["var x = pm.iterationData.get('k');"] }
            }]
          }]
        }
        """;
        var diags = new List<TranslationDiagnostic>();
        _ = PostmanV2Importer.ImportFromJson(json, new PostmanImportOptions(
            TranslateScripts: true,
            OnDiagnostic: diags.Add));

        diags.Should().HaveCount(1);
        diags[0].RequestName.Should().Be("weird");
        diags[0].Phase.Should().Be("pre-request");
        diags[0].UnhandledTokens.Should().Contain(t => t.StartsWith("pm.iterationData"));
    }
}
