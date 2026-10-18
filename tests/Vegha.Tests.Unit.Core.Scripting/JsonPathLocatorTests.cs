using System.Text.Json.Nodes;
using FluentAssertions;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class JsonPathLocatorTests
{
    private const string Pretty = """
        {
          "access_token": "abc123",
          "user": {
            "id": 42,
            "user-name": "ada"
          },
          "items": [
            { "id": 1 },
            { "id": 2, "tags": ["x", "y"] }
          ]
        }
        """;

    private static JsonPathLocator.Location Locate(string needle, int delta = 1)
    {
        var at = Pretty.IndexOf(needle, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, needle);
        return JsonPathLocator.Locate(Pretty, at + delta)!;
    }

    [Theory]
    [InlineData("\"abc123\"", "access_token", "access_token")]
    [InlineData("\"access_token\"", "access_token", "access_token")]
    [InlineData("42", "user.id", "id")]
    [InlineData("\"ada\"", "user['user-name']", "userName")]
    [InlineData("{ \"id\": 1 }", "items[0]", "items")]
    [InlineData("\"y\"", "items[1].tags[1]", "tags")]
    public void Locates_value_under_caret(string needle, string path, string name)
    {
        var loc = Locate(needle);
        loc.Path.Should().Be(path);
        loc.SuggestedName.Should().Be(name);
    }

    [Fact]
    public void Caret_on_container_bracket_selects_the_container()
    {
        var loc = Locate("\"user\": {", delta: "\"user\": ".Length);
        loc.Path.Should().Be("user");
        loc.IsContainer.Should().BeTrue();
    }

    [Fact]
    public void Located_paths_resolve_back_to_the_same_value()
    {
        var snap = new ResponseSnapshot(200, "OK", Pretty, 1, null);
        var loc = Locate("\"y\"");
        ResponseValueResolver.Resolve(ResponseExpression.Create(ResponseSource.Body, loc.Path), snap)
            .Value.ToVariableString().Should().Be("y");
    }

    [Fact]
    public void Invalid_json_returns_what_was_resolved()
    {
        JsonPathLocator.Locate("{\"a\": 1, \"b\": ", 4)!.Path.Should().Be("a");
        JsonPathLocator.Locate("", 0).Should().BeNull();
    }

    [Theory]
    [InlineData("access_token", "access_token")]
    [InlineData("user-id", "userId")]
    [InlineData("X-Request-Id", "XRequestId")]
    [InlineData("2fa", "_2fa")]
    [InlineData("", "value")]
    public void Variable_name_suggestions(string key, string expected) =>
        JsonPathLocator.ToVariableName(key).Should().Be(expected);

    [Fact]
    public void Suggester_lists_paths_and_headers()
    {
        var paths = ResponsePathSuggester.JsonPaths(JsonNode.Parse(Pretty));
        paths.Should().ContainInOrder("access_token", "user", "items", "user.id", "user['user-name']", "items[0]", "items[-1]", "items[0].id");

        var snap = new ResponseSnapshot(200, "OK", Pretty, 1,
            new[] { new KeyValuePair<string, string>("ETag", "x"), new KeyValuePair<string, string>("Set-Cookie", "sid=1") });
        ResponsePathSuggester.For(ResponseSource.Header, snap).Should().Contain("ETag");
        ResponsePathSuggester.For(ResponseSource.Cookie, snap).Should().Equal("sid");
        ResponsePathSuggester.For(ResponseSource.JsonPath, snap).Should().Contain("$.user.id");
    }

    [Theory]
    [InlineData(ResponseSource.Body, "items | first | prop(\"id\")", "res.body.items | first | prop(\"id\")")]
    [InlineData(ResponseSource.Body, "$.items[*].id", "jsonpath('$.items[*].id')")]
    [InlineData(ResponseSource.Body, "res.body.user.id", "res.body.user.id")]
    [InlineData(ResponseSource.Header, "X-Id | lower", "res.headers['X-Id'] | lower")]
    [InlineData(ResponseSource.Status, "", "res.status")]
    [InlineData(ResponseSource.Literal, "a | b", "'a | b'")]
    public void Editor_text_maps_to_storage_and_back(ResponseSource source, string text, string storage)
    {
        var expr = ResponseExpression.FromEditor(source, text);
        expr.ToStorage().Should().Be(storage);
        ResponseExpression.Parse(storage).ToStorage().Should().Be(storage);
    }
}
