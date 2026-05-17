using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Disk-level operations that apply drift between an OpenAPI spec and an on-disk Bruno
/// collection. Three flavors:
///
/// 1. <see cref="WriteAddedFolder"/> — drops new operations into a sub-folder of the
///    existing collection without touching anything else. Used by "Import added ops".
/// 2. <see cref="DeleteRequestFiles"/> — removes specific <c>.bru</c> files. Used by
///    "Delete removed ops".
/// 3. <see cref="ReplaceCollection"/> — clears every <c>.bru</c> file and Bruno folder
///    tree under the root, then rewrites from the spec. Auxiliary content
///    (<c>environments/</c>, <c>.git/</c>, README, etc.) is left untouched. Used by
///    "Replace collection with spec".
/// </summary>
public static class OpenApiSpecApplier
{
    /// <summary>Writes <paramref name="added"/> as a new Bruno folder under
    /// <paramref name="collectionRootPath"/>. Existing files are untouched. Returns the
    /// folder's absolute path. The folder name is sanitized for the filesystem; on
    /// collision, " (N)" is appended until a free slot is found.</summary>
    public static string WriteAddedFolder(
        string collectionRootPath,
        IReadOnlyCollection<RequestItem> added,
        string folderName)
    {
        if (string.IsNullOrEmpty(collectionRootPath))
            throw new ArgumentException("Collection root path is required.", nameof(collectionRootPath));
        if (added is null || added.Count == 0)
            throw new ArgumentException("At least one request is required.", nameof(added));

        Directory.CreateDirectory(collectionRootPath);

        var stem = Sanitize(folderName);
        if (string.IsNullOrEmpty(stem)) stem = "from-spec";
        var newFolder = Path.Combine(collectionRootPath, stem);
        for (var i = 2; Directory.Exists(newFolder) && i < 1000; i++)
            newFolder = Path.Combine(collectionRootPath, $"{stem} ({i})");
        Directory.CreateDirectory(newFolder);

        var folderMeta = new Folder { Name = folderName };
        File.WriteAllText(Path.Combine(newFolder, "folder.bru"), BruMetaEmitter.EmitFolder(folderMeta));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in added)
        {
            var fileStem = UniqueStem(used, Sanitize(req.Name));
            File.WriteAllText(Path.Combine(newFolder, fileStem + ".bru"), BruEmitter.Emit(req));
        }

        return newFolder;
    }

    /// <summary>Deletes the .bru files at the given paths. Silently skips missing files
    /// and entries outside the collection root (defensive — callers pass tree-derived
    /// paths which should always be inside the root). Returns the number actually
    /// removed.</summary>
    public static int DeleteRequestFiles(string collectionRootPath, IEnumerable<string> filePaths)
    {
        if (string.IsNullOrEmpty(collectionRootPath)) return 0;
        var rootFull = Path.GetFullPath(collectionRootPath);
        var deleted = 0;
        foreach (var path in filePaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { continue; }
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;
            try { File.Delete(full); deleted++; }
            catch { /* swallow — best-effort */ }
        }
        return deleted;
    }

    /// <summary>Replaces the entire Bruno tree under <paramref name="collectionRootPath"/>
    /// with the contents of <paramref name="newCollection"/>. Top-level .bru files and
    /// subfolders that contain .bru files are wiped; non-Bruno files (environments/,
    /// .git/, READMEs) are preserved. After the wipe, the new collection is written via
    /// <see cref="BruCollectionWriter"/>.</summary>
    public static void ReplaceCollection(string collectionRootPath, Collection newCollection)
    {
        if (string.IsNullOrEmpty(collectionRootPath))
            throw new ArgumentException("Collection root path is required.", nameof(collectionRootPath));
        if (!Directory.Exists(collectionRootPath))
            Directory.CreateDirectory(collectionRootPath);

        CleanBruArtifacts(collectionRootPath);
        BruCollectionWriter.Write(collectionRootPath, newCollection);
    }

    /// <summary>Removes loose .bru files at the root and recursively deletes any
    /// subdirectory that looks like a Bruno folder tree (has a folder.bru anywhere
    /// inside, or contains any .bru file). Preserves directories that have no .bru
    /// content (environments/, .git/, docs/, etc.).</summary>
    private static void CleanBruArtifacts(string rootDir)
    {
        foreach (var bru in Directory.EnumerateFiles(rootDir, "*.bru", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(bru); } catch { /* best-effort */ }
        }
        foreach (var sub in Directory.EnumerateDirectories(rootDir))
        {
            bool hasBru;
            try { hasBru = Directory.EnumerateFiles(sub, "*.bru", SearchOption.AllDirectories).Any(); }
            catch { hasBru = false; }
            if (hasBru)
            {
                try { Directory.Delete(sub, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    private static string UniqueStem(HashSet<string> used, string proposed)
    {
        var stem = string.IsNullOrEmpty(proposed) ? "request" : proposed;
        if (used.Add(stem)) return stem;
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{stem}-{i}";
            if (used.Add(candidate)) return candidate;
        }
        return stem + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? string.Empty : s;
    }
}
