using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Writes a <see cref="Collection"/> to disk in the Bruno-style layout that
/// <see cref="CollectionLoader"/> reads back: <c>collection.bru</c> at the root,
/// each request as a <c>{name}.bru</c> file, subfolders recursed with their own
/// <c>folder.bru</c>. Used by the Import wizard so an imported Postman / WSDL /
/// OpenAPI / Insomnia collection lands in the same on-disk format the rest of
/// the app speaks — file watcher, save, clone, properties dialog all just work.
///
/// Distinct from <see cref="Vegha.Core.FileFormat.CollectionStore"/>'s JSON
/// format, which is the historical persistence layer not yet wired into the
/// tree loader.
/// </summary>
public static class BruCollectionWriter
{
    /// <param name="includeEnvironments">Also write <see cref="Collection.Environments"/> as
    /// <c>environments/{name}.bru</c> (secret values are left out of the file, as Vegha does
    /// for every environment). Off by default for callers that place environments themselves.</param>
    public static void Write(string rootDirectory, Collection collection, bool includeEnvironments = false)
    {
        Directory.CreateDirectory(rootDirectory);

        // Root meta — name, vars, headers, auth, scripts, docs.
        File.WriteAllText(
            Path.Combine(rootDirectory, "collection.bru"),
            BruMetaEmitter.EmitCollection(collection));

        WriteFolderContents(rootDirectory, collection.Requests, collection.Folders);

        if (includeEnvironments && collection.Environments.Count > 0)
        {
            var envDir = Path.Combine(rootDirectory, "environments");
            Directory.CreateDirectory(envDir);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var env in collection.Environments)
                File.WriteAllText(Path.Combine(envDir, UniqueStem(used, Sanitize(env.Name)) + ".bru"),
                    BruMetaEmitter.EmitEnvironment(env, includeSecretValues: false));
        }
    }

    private static void WriteFolderContents(string dir, IList<RequestItem> requests, IList<Folder> folders)
    {
        Directory.CreateDirectory(dir);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in requests)
        {
            var fileStem = UniqueStem(usedNames, Sanitize(req.Name));
            File.WriteAllText(Path.Combine(dir, fileStem + ".bru"), BruEmitter.Emit(req));
        }

        foreach (var folder in folders)
        {
            var folderDir = Path.Combine(dir, Sanitize(folder.Name));
            Directory.CreateDirectory(folderDir);
            File.WriteAllText(Path.Combine(folderDir, "folder.bru"), BruMetaEmitter.EmitFolder(folder));
            WriteFolderContents(folderDir, folder.Requests, folder.Folders);
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
        // Safety valve — should be unreachable for sane WSDLs.
        return stem + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }
}
