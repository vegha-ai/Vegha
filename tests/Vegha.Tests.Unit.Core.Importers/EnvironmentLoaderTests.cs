using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.Importers;

public class EnvironmentLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public EnvironmentLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Vegha-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string Write(string fileName, string content)
    {
        var p = Path.Combine(_tempDir, fileName);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void LoadDirectory_RestoresStrippedSecretValue_FromEncryptedSidecar()
    {
        // Simulate the env-panel save path: strip literal secrets into the sidecar, then
        // serialize the (blanked) .env.json the way the app writes it.
        var envDir = Path.Combine(_tempDir, "environments");
        Directory.CreateDirectory(envDir);

        var env = new DomainEnv
        {
            Id = "env-local",
            Name = "Local",
            Variables = new List<Vegha.Core.Domain.KvPair>
            {
                new("host", "api.example.com"),
                new("api_key", "literal-secret-9999"),
            },
            SecretVariables = new List<string> { "api_key" },
        };

        var store = new Vegha.Core.Persistence.EnvironmentSecretStore();
        var stripped = Vegha.Core.FileFormat.EnvironmentSecretSplitter.StripForPersistence(env, _tempDir, store);
        File.WriteAllText(
            Path.Combine(envDir, "Local.env.json"),
            Vegha.Core.FileFormat.CollectionJson.SerializeEnvironment(
                Vegha.Core.FileFormat.EnvironmentFile.FromDomain(stripped)));

        var loaded = EnvironmentLoader.LoadDirectory(envDir);

        var local = loaded.Single();
        local.Variables.Single(v => v.Name == "api_key").Value.Should().Be("literal-secret-9999");
        local.Variables.Single(v => v.Name == "host").Value.Should().Be("api.example.com");
    }

    [Fact]
    public void Load_VarsBlock_OnlyVarsParsed()
    {
        var path = Write("Local.bru", """
            vars {
              host: http://localhost:8080
              api_key: dev-key
            }
            """);

        var env = EnvironmentLoader.Load(path);

        env.Should().NotBeNull();
        env!.Name.Should().Be("Local");
        env.Variables.Should().HaveCount(2);
        env.Variables.Should().Contain(v => v.Name == "host" && v.Value == "http://localhost:8080");
        env.Variables.Should().Contain(v => v.Name == "api_key" && v.Value == "dev-key");
    }

    [Fact]
    public void Load_StripsAndRecordsSecretsBlock_DoesNotBreakParse()
    {
        // Bruno's env files use vars:secret [ a, b, c ] which is NOT standard grammar.
        // The loader strips this block before parsing and records the names separately.
        var path = Write("Prod.bru", """
            vars {
              host: https://api.acme.io
              client_id: known
            }
            vars:secret [
              client_secret,
              api_token,
              admin_password
            ]
            """);

        var env = EnvironmentLoader.Load(path);

        env.Should().NotBeNull();
        env!.Variables.Should().HaveCount(2);
        env.SecretVariables.Should().BeEquivalentTo(new[] { "client_secret", "api_token", "admin_password" });
    }

    [Fact]
    public void Load_SecretsBlock_AloneStillParses()
    {
        var path = Write("OnlySecrets.bru", """
            vars:secret [
              x,
              y
            ]
            """);

        var env = EnvironmentLoader.Load(path);

        env.Should().NotBeNull();
        env!.Variables.Should().BeEmpty();
        env.SecretVariables.Should().Equal("x", "y");
    }

    [Fact]
    public void Load_HandlesDisabledVars()
    {
        var path = Write("E.bru", """
            vars {
              active: yes
              ~inactive: no
            }
            """);

        var env = EnvironmentLoader.Load(path)!;
        env.Variables.Should().Contain(v => v.Name == "active" && v.Enabled);
        env.Variables.Should().Contain(v => v.Name == "inactive" && !v.Enabled);
    }

    [Fact]
    public void LoadDirectory_LoadsAllEnvFiles_Sorted()
    {
        Write("Prod.bru", "vars { host: https://prod }");
        Write("Local.bru", "vars { host: http://localhost }");
        Write("Staging.bru", "vars { host: https://staging }");

        var envs = EnvironmentLoader.LoadDirectory(_tempDir);

        envs.Select(e => e.Name).Should().Equal("Local", "Prod", "Staging");
    }

    [Fact]
    public void LoadDirectory_MissingDir_ReturnsEmpty()
    {
        var envs = EnvironmentLoader.LoadDirectory(Path.Combine(_tempDir, "nope"));
        envs.Should().BeEmpty();
    }

    [Fact]
    public void Load_RealBrunoFixture_ParsesVarsAndSecrets()
    {
        // Mirror of bruno-tests/collection/environments/Local.bru
        var path = Write("Local.bru", """
            vars {
              host: http://localhost:8080
              client_id: client_id_1
              foo: bar
            }
            vars:secret [
              github_client_secret,
              google_client_secret,
              github_authorization_code
            ]
            """);

        var env = EnvironmentLoader.Load(path)!;
        env.Name.Should().Be("Local");
        env.Variables.Should().HaveCount(3);
        env.SecretVariables.Should().HaveCount(3);
    }
}
