using System.Text.Json.Nodes;
using Vegha.Core.Importers;

namespace Vegha.Core.Exporters;

/// <summary>
/// Exports a collection as a Bruno folder tree (zipped by <see cref="ExportService"/>). When
/// the collection is backed by a folder on disk, that folder is copied verbatim — it's
/// already Bruno's format, so the export is lossless and keeps JS helper files. Otherwise
/// the in-memory collection is serialized through <see cref="BruCollectionWriter"/>.
///
/// Never copied: VCS/build folders (<see cref="CollectionLoader.IgnoredFolders"/>) and the
/// <c>.secrets/</c> sidecar (machine-bound encrypted values). The <c>environments/</c> folder
/// is re-emitted from the loaded environments as Bruno <c>.bru</c> env files so the secret
/// policy applies and Bruno can read them.
/// </summary>
public static class BrunoExporter
{
    private static readonly HashSet<string> _excludedFolders =
        new(CollectionLoader.IgnoredFolders.Append(".secrets"), StringComparer.OrdinalIgnoreCase);

    /// <param name="rootName">Folder name every entry is placed under (forward slashes).</param>
    public static List<ExportedFile> Export(ExportSource source, string rootName, ExportOptions options)
    {
        var files = new List<ExportedFile>();
        var collection = source.Collection;

        if (!string.IsNullOrEmpty(source.SourcePath) && Directory.Exists(source.SourcePath))
        {
            CopyTree(source.SourcePath, rootName, files);
        }
        else
        {
            var temp = Path.Combine(Path.GetTempPath(), "Vegha-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                BruCollectionWriter.Write(temp, collection);
                CopyTree(temp, rootName, files);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* best-effort cleanup */ }
            }
        }

        // Bruno only opens a folder that carries bruno.json.
        if (!files.Any(f => f.RelativePath.Equals(rootName + "/bruno.json", StringComparison.OrdinalIgnoreCase)))
        {
            var manifest = new JsonObject
            {
                ["version"] = "1",
                ["name"] = collection.Name,
                ["type"] = "collection",
                ["ignore"] = new JsonArray("node_modules", ".git"),
            };
            files.Add(new ExportedFile(rootName + "/bruno.json", ExportNaming.ToUtf8(manifest)));
        }

        if (options.IncludeEnvironments)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var env in collection.Environments)
            {
                var stem = ExportNaming.Unique(used, ExportNaming.Sanitize(env.Name));
                files.Add(new ExportedFile(
                    $"{rootName}/environments/{stem}.bru",
                    ExportNaming.ToUtf8(BruMetaEmitter.EmitEnvironment(env, options.IncludeSecrets))));
            }
        }
        return files;
    }

    private static void CopyTree(string root, string rootName, List<ExportedFile> files)
    {
        foreach (var file in EnumerateFiles(root, root))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new ExportedFile(rootName + "/" + rel, File.ReadAllBytes(file)));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string dir, string root)
    {
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            yield return f;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            if (_excludedFolders.Contains(name)) continue;
            // Root-level environments/ is re-emitted from the loaded envs (see class remarks).
            if (string.Equals(dir, root, StringComparison.Ordinal)
                && CollectionLoader.ReservedFolders.Contains(name)) continue;
            foreach (var f in EnumerateFiles(sub, root)) yield return f;
        }
    }
}
