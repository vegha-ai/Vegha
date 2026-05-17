using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using FluentAssertions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.FileFormat;

public class EnvironmentFileTests
{
    [Fact]
    public void Color_RoundTrips_Through_Serialize_Deserialize()
    {
        var env = new DomainEnv
        {
            Name = "Local",
            Color = "#10B981",
            Variables = new List<KvPair> { new("baseUrl", "https://x.test") },
        };

        var json = CollectionJson.SerializeEnvironment(EnvironmentFile.FromDomain(env));
        json.Should().Contain("#10B981");

        var back = CollectionJson.DeserializeEnvironment(json);
        back.Should().NotBeNull();
        EnvironmentFile.ToDomain(back!).Color.Should().Be("#10B981");
    }

    [Fact]
    public void Null_Color_Is_Omitted_From_Output()
    {
        var env = new DomainEnv { Name = "Local" };
        var json = CollectionJson.SerializeEnvironment(EnvironmentFile.FromDomain(env));
        json.Should().NotContain("color");
    }

    [Fact]
    public void Existing_Env_File_Without_Color_Deserializes_With_Null()
    {
        const string json = """
            { "name": "Legacy", "variables": [{ "name": "k", "value": "v", "enabled": true }] }
            """;
        var file = CollectionJson.DeserializeEnvironment(json);
        file.Should().NotBeNull();
        var domain = EnvironmentFile.ToDomain(file!);
        domain.Color.Should().BeNull();
        domain.Name.Should().Be("Legacy");
    }
}
