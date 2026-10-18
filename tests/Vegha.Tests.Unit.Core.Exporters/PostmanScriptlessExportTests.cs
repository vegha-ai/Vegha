using System.Text.Json.Nodes;
using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Exporters;

/// <summary>Scriptless captures / assertions / request vars become pm.* lines in the Postman
/// export, in Vegha's execution order; linked requests are reported, not silently dropped.</summary>
public class PostmanScriptlessExportTests
{
    private static (JsonObject Root, List<string> Warnings) Export(Collection c)
    {
        var warnings = new List<string>();
        var bytes = PostmanV2Exporter.Export(c, new ExportOptions(), warnings);
        return (JsonNode.Parse(ExportFixtures.Text(bytes))!.AsObject(), warnings);
    }

    private static string Exec(JsonNode? node, string listen) =>
        string.Join("\n", node!["event"]!.AsArray()
            .Single(e => (string?)e!["listen"] == listen)!["script"]!["exec"]!.AsArray()
            .Select(l => (string?)l));

    [Fact]
    public void Request_scriptless_parts_are_exported_in_order()
    {
        var request = new RequestItem
        {
            Name = "Login",
            Method = "POST",
            Url = "https://api.test/login",
            PreRequestVars = { new KvPair("user", "{{base}}-u") },
            PostResponseVars = { new KvPair("token", "res.body.access_token") },
            PostResponseScript = "console.log('post');",
            Assertions = { new KvPair("res.status", "eq 200") },
            Tests = "test('t', () => {});",
            ChainLinks = { new KvPair("Other.bru", "always") },
        };
        var (root, warnings) = Export(new Collection { Name = "C", Requests = { request } });
        var item = root["item"]![0];

        Exec(item, "prerequest").Should().Contain("pm.variables.set(\"user\", pm.variables.replaceIn(\"{{base}}-u\"));");

        var test = Exec(item, "test");
        var capture = test.IndexOf("pm.variables.set(\"token\"", StringComparison.Ordinal);
        var post = test.IndexOf("console.log('post');", StringComparison.Ordinal);
        var assertion = test.IndexOf("pm.test(\"res.status eq 200\"", StringComparison.Ordinal);
        var js = test.IndexOf("pm.test('t'", StringComparison.Ordinal);
        new[] { capture, post, assertion, js }.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder();

        warnings.Should().Contain(w => w.Contains("linked requests") && w.Contains("1 item"));
        warnings.Should().NotContain(w => w.Contains("request-level vars"));
    }

    [Fact]
    public void Collection_and_folder_assertions_go_to_their_test_events()
    {
        var folder = new Folder { Name = "users", Assertions = { new KvPair("res.responseTime", "lt 2000") } };
        var c = new Collection
        {
            Name = "C",
            Folders = { folder },
            Assertions = { new KvPair("res.status", "lt 500") },
        };
        var (root, warnings) = Export(c);
        Exec(root, "test").Should().Contain("to.be.below(500)");
        Exec(root["item"]![0], "test").Should().Contain("to.be.below(2000)");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Untranslatable_expressions_are_commented_and_warned()
    {
        var request = new RequestItem
        {
            Name = "X",
            Url = "https://api.test/x",
            PostResponseVars = { new KvPair("ids", "jsonpath('$..id')") },
        };
        var (root, warnings) = Export(new Collection { Name = "C", Requests = { request } });
        Exec(root["item"]![0], "test").Should().Contain("// Vegha capture not translated: ids = jsonpath('$..id')");
        warnings.Should().ContainSingle(w => w.Contains("no Postman equivalent") && w.Contains("ids = jsonpath"));
    }

    [Fact]
    public void Vegha_export_round_trips_back_to_rows()
    {
        var request = new RequestItem
        {
            Name = "Login",
            Method = "POST",
            Url = "https://api.test/login",
            PostResponseVars = { new KvPair("token", "res.body.access_token") },
            PostResponseEnvVars = { new KvPair("sid", "res.headers['x-session']") },
            Assertions =
            {
                new KvPair("res.status", "eq 200"),
                new KvPair("res.body.items | count", "between 1, 5"),
                new KvPair("res.body.user.email", "isEmail"),
            },
        };
        var warnings = new List<string>();
        var json = System.Text.Encoding.UTF8.GetString(
            PostmanV2Exporter.Export(new Collection { Name = "C", Requests = { request } }, new ExportOptions(), warnings));

        var back = PostmanV2Importer.ImportFromJson(json, new PostmanImportOptions(TranslateScripts: true)).Requests.Single();

        back.PostResponseVars.Should().Equal(new KvPair("token", "res.body.access_token"));
        back.PostResponseEnvVars.Should().Equal(new KvPair("sid", "res.headers['x-session']"));
        back.Assertions.Should().Equal(request.Assertions);
        back.Tests.Should().BeNull();
    }
}
