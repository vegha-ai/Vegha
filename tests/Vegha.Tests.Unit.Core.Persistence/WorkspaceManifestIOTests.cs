using System.IO;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

/// <summary>Round-trip tests for the Bruno-compatible <c>workspace.yml</c> reader/writer.</summary>
public class WorkspaceManifestIOTests
{
    [Fact]
    public void Write_Then_Read_RoundTripsName_Version_Created()
    {
        using var tmp = new TempDir();
        var manifest = new WorkspaceManifest
        {
            Version = 1,
            Name = "My Workspace",
            Created = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
        };

        WorkspaceManifestIO.Write(tmp.Path, manifest);
        var loaded = WorkspaceManifestIO.Read(tmp.Path);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.Name.Should().Be("My Workspace");
    }

    [Fact]
    public void Read_ReturnsNull_WhenManifestMissing()
    {
        using var tmp = new TempDir();
        var loaded = WorkspaceManifestIO.Read(tmp.Path);
        loaded.Should().BeNull();
    }

    [Fact]
    public void Write_CreatesFolderIfMissing()
    {
        using var tmp = new TempDir();
        var nested = Path.Combine(tmp.Path, "nested-ws");

        WorkspaceManifestIO.Write(nested, new WorkspaceManifest { Name = "X" });

        WorkspaceManifestIO.Exists(nested).Should().BeTrue();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vegha-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
