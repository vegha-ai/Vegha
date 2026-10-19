using FluentAssertions;
using Vegha.Integrations.Git;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

public sealed class GitSecretScannerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "vegha-scan-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();

    public GitSecretScannerTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData("  auth:bearer { token: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB }")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("  x-api-key: Ab3dEf9Hij2KlmN0pq")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void ScanLine_FlagsSecrets(string line) => GitSecretScanner.ScanLine(line).Should().NotBeNull();

    [Theory]
    [InlineData("  auth:bearer { token: {{accessToken}} }")]
    [InlineData("  password: {{password}}")]
    [InlineData("  token: secret://vault/kv/api#token")]
    [InlineData("  password: please change me")]
    [InlineData("  url: https://api.example.com/users")]
    [InlineData("  apiKey: ********")]
    public void ScanLine_IgnoresVariablesAndProse(string line) => GitSecretScanner.ScanLine(line).Should().BeNull();

    [Fact]
    public void ScanLine_MasksTheValue()
    {
        var hit = GitSecretScanner.ScanLine("token: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB")!;
        hit.Value.Masked.Should().NotContain("0123456789abcdefghij");
    }

    [Fact]
    public void ScanStaged_ReportsFileAndLine_ForAddedLinesOnly()
    {
        _git.Init(_repo);
        File.WriteAllText(Path.Combine(_repo, "get.bru"), "meta {\n  name: get\n}\n");
        _git.Stage(_repo, "get.bru");
        _git.Commit(_repo, "seed", "T", "t@example.com");

        File.WriteAllText(Path.Combine(_repo, "get.bru"), "meta {\n  name: get\n}\nheaders {\n  x-api-key: Zx9Yw8Vu7Ts6Rq5P\n}\n");
        _git.Stage(_repo, "get.bru");

        var findings = GitSecretScanner.ScanStaged(_git, _repo);

        findings.Should().ContainSingle();
        findings[0].Path.Should().Be("get.bru");
        findings[0].Line.Should().Be(5);
    }

    [Theory]
    [InlineData("  token: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("  password: \"S3cret!Passw0rd\"", "S3cret!Passw0rd")]
    public void SharedPatterns_ReportWhereTheSecretIs(string line, string secret)
    {
        // Editors (the VS Code extension's diagnostics) underline exactly the secret.
        var m = Vegha.Core.Domain.Security.SecretPatterns.ScanLine(line)!;
        line.Substring(m.Start, m.Length).Should().Be(secret);
        m.MaskedLine.Should().NotContain(secret);
    }
}
