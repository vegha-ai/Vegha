using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vegha.Core.Persistence;

/// <summary>
/// In-memory representation of <c>workspace.yml</c> at the root of a Bruno-compatible
/// workspace folder. The schema is intentionally tiny — name, version, created — to
/// stay round-trippable with Bruno itself.
/// </summary>
public sealed record WorkspaceManifest
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form settings bag. Reserved for future per-workspace settings (e.g.,
    /// default request timeout) — Bruno itself uses this slot the same way.</summary>
    public Dictionary<string, string> Settings { get; init; } = new();
}

/// <summary>YAML reader/writer for <see cref="WorkspaceManifest"/>. Marker file name is
/// <c>workspace.yml</c> (Bruno-compatible). The file lives at the root of the workspace
/// folder, peer to <c>collections/</c> and <c>environments/</c>.</summary>
public static class WorkspaceManifestIO
{
    public const string FileName = "workspace.yml";

    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer s_serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Returns true when <paramref name="folder"/> contains a <c>workspace.yml</c>.</summary>
    public static bool Exists(string folder) =>
        File.Exists(Path.Combine(folder, FileName));

    /// <summary>Reads the manifest. Throws on malformed YAML; returns null when the file
    /// is missing — callers decide whether that's an error.</summary>
    public static WorkspaceManifest? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        var yaml = File.ReadAllText(path);
        return s_deserializer.Deserialize<WorkspaceManifest>(yaml) ?? new WorkspaceManifest();
    }

    /// <summary>Writes (or rewrites) the manifest at the given folder. Creates the folder
    /// if missing.</summary>
    public static void Write(string folder, WorkspaceManifest manifest)
    {
        Directory.CreateDirectory(folder);
        var yaml = s_serializer.Serialize(manifest);
        File.WriteAllText(Path.Combine(folder, FileName), yaml);
    }
}
