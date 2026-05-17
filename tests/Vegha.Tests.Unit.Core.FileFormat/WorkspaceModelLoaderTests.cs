using System.IO;
using Vegha.Core.FileFormat;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.FileFormat;

/// <summary>Validates <see cref="WorkspaceModelLoader"/> parsing of
/// <c>&lt;workspace&gt;/environments/*.env.json</c> + <c>&lt;workspace&gt;/scripts/*.js</c>.</summary>
public class WorkspaceModelLoaderTests : IDisposable
{
    private readonly string _tmp;

    public WorkspaceModelLoaderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "Vegha-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void MissingWorkspaceFolder_ReturnsEmpty()
    {
        var loaded = WorkspaceModelLoader.Load(Path.Combine(_tmp, "does-not-exist"));
        loaded.Environments.Should().BeEmpty();
        loaded.PreRequestScript.Should().BeNull();
        loaded.TestsScript.Should().BeNull();
    }

    [Fact]
    public void EnvironmentsFolder_Parsed()
    {
        var envDir = Path.Combine(_tmp, "environments");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, "Local.env.json"), """
            { "Name": "Local", "Variables": [
                { "Name": "baseUrl", "Value": "http://local.test", "Enabled": true }
            ]}
            """);

        var loaded = WorkspaceModelLoader.Load(_tmp);
        loaded.Environments.Should().ContainSingle();
        var env = loaded.Environments[0];
        env.Name.Should().Be("Local");
        env.Variables.Should().ContainSingle();
        env.Variables[0].Name.Should().Be("baseUrl");
        env.Variables[0].Value.Should().Be("http://local.test");
    }

    [Fact]
    public void ScriptsFolder_BothFiles_ReadAsText()
    {
        var scriptsDir = Path.Combine(_tmp, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "pre-request.js"), "console.log('pre');");
        File.WriteAllText(Path.Combine(scriptsDir, "tests.js"), "tests.assert(1==1);");

        var loaded = WorkspaceModelLoader.Load(_tmp);
        loaded.PreRequestScript.Should().Be("console.log('pre');");
        loaded.TestsScript.Should().Be("tests.assert(1==1);");
    }

    [Fact]
    public void MalformedEnvFile_Skipped_NotThrown()
    {
        var envDir = Path.Combine(_tmp, "environments");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, "broken.env.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(envDir, "Ok.env.json"),
            """{ "Name": "Ok", "Variables": [] }""");

        var loaded = WorkspaceModelLoader.Load(_tmp);
        loaded.Environments.Should().ContainSingle()
            .Which.Name.Should().Be("Ok");
    }
}
