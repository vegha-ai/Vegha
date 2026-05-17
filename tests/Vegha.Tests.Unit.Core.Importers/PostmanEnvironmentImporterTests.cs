using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class PostmanEnvironmentImporterTests
{
    [Fact]
    public void Imports_NameAndValues_PopulatesEnvironment()
    {
        const string json = """
            {
              "id": "abc",
              "name": "Production",
              "values": [
                { "key": "baseUrl", "value": "https://api.acme.io", "enabled": true, "type": "default" },
                { "key": "apiKey", "value": "SECRET", "enabled": true, "type": "secret" },
                { "key": "disabled", "value": "x", "enabled": false }
              ]
            }
            """;

        var env = PostmanEnvironmentImporter.ImportFromString(json);

        env.Name.Should().Be("Production");
        env.Variables.Should().HaveCount(3);

        env.Variables[0].Name.Should().Be("baseUrl");
        env.Variables[0].Value.Should().Be("https://api.acme.io");
        env.Variables[0].Enabled.Should().BeTrue();

        env.Variables[2].Enabled.Should().BeFalse();

        env.SecretVariables.Should().Contain("apiKey");
        env.SecretVariables.Should().NotContain("baseUrl");
    }

    [Fact]
    public void NormalizesInvalidVariableNames_ToUnderscores()
    {
        const string json = """
            {
              "name": "X",
              "values": [
                { "key": "my var", "value": "1" },
                { "key": "a.b.c", "value": "2" }
              ]
            }
            """;

        var env = PostmanEnvironmentImporter.ImportFromString(json);
        env.Variables[0].Name.Should().Be("my_var");
        env.Variables[1].Name.Should().Be("a_b_c");
    }

    [Fact]
    public void EmptyValues_ReturnsEmptyEnvironment()
    {
        const string json = """{ "name": "Empty", "values": [] }""";
        var env = PostmanEnvironmentImporter.ImportFromString(json);
        env.Name.Should().Be("Empty");
        env.Variables.Should().BeEmpty();
    }

    [Fact]
    public void MissingName_DefaultsToUntitled()
    {
        const string json = """{ "values": [{ "key": "x", "value": "y" }] }""";
        var env = PostmanEnvironmentImporter.ImportFromString(json);
        env.Name.Should().Be("Untitled");
    }

    [Fact]
    public void EntriesMissingKey_AreSkipped()
    {
        const string json = """
            {
              "name": "X",
              "values": [
                { "value": "no-key-here" },
                { "key": "ok", "value": "1" }
              ]
            }
            """;
        var env = PostmanEnvironmentImporter.ImportFromString(json);
        env.Variables.Should().ContainSingle();
        env.Variables[0].Name.Should().Be("ok");
    }
}
