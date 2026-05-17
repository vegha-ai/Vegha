using Vegha.Core.Domain;

namespace Vegha.Core.FileFormat;

/// <summary>
/// Reads and writes the JSON-native collection format (manifest + per-request files +
/// environments folder) to disk. The folder layout is:
/// <code>
/// &lt;collectionRoot&gt;/
///   collection.json
///   &lt;request&gt;.req.json
///   &lt;folder&gt;/
///     &lt;nested-request&gt;.req.json
///   environments/
///     &lt;env&gt;.env.json
/// </code>
/// </summary>
public static class CollectionStore
{
    public static Collection Load(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Collection root not found: {root}");

        var manifestPath = Path.Combine(root, CollectionJson.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Missing {CollectionJson.ManifestFileName} at {root}");

        var manifest = CollectionJson.DeserializeManifest(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException($"Could not parse {manifestPath}");

        // All *.req.json under the root (excluding environments/).
        var envDir = Path.Combine(root, CollectionJson.EnvironmentsFolder);
        var requestFiles = Directory.EnumerateFiles(root, "*" + CollectionJson.RequestSuffix, SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(envDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var loadedRequests = new List<(RequestFile req, string folderPath)>();
        foreach (var path in requestFiles)
        {
            var req = CollectionJson.DeserializeRequest(File.ReadAllText(path));
            if (req is null) continue;
            var folderPath = ToFolderPath(root, path);
            loadedRequests.Add((req, folderPath));
        }

        // folder.json metadata files: keyed by their forward-slash folder path so the
        // composition layer can attach them to the right Folder during ToCollection.
        var folderMetas = new Dictionary<string, FolderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, CollectionJson.FolderManifestFileName, SearchOption.AllDirectories))
        {
            if (path.StartsWith(envDir, StringComparison.OrdinalIgnoreCase)) continue;
            var folderPath = ToFolderPath(root, path);
            if (string.IsNullOrEmpty(folderPath)) continue;
            var folder = CollectionJson.DeserializeFolder(File.ReadAllText(path));
            if (folder is not null) folderMetas[folderPath] = folder;
        }

        var envs = new List<EnvironmentFile>();
        if (Directory.Exists(envDir))
        {
            foreach (var path in Directory.EnumerateFiles(envDir, "*" + CollectionJson.EnvironmentSuffix))
            {
                var env = CollectionJson.DeserializeEnvironment(File.ReadAllText(path));
                if (env is not null) envs.Add(env);
            }
        }

        var collection = CollectionJson.ToCollection(manifest, loadedRequests, envs);
        if (folderMetas.Count > 0) ApplyFolderMetadata(collection.Folders, string.Empty, folderMetas);

        // Restore literal secret values from the encrypted sidecar (or VEGHA_SECRET_*
        // overrides) so the in-memory environments carry full values for UI + execution.
        var secretStore = new Persistence.EnvironmentSecretStore();
        for (var i = 0; i < collection.Environments.Count; i++)
            collection.Environments[i] =
                EnvironmentSecretSplitter.MergeFromStore(collection.Environments[i], root, secretStore);

        return collection;
    }

    private static void ApplyFolderMetadata(IList<Folder> folders, string prefix, IReadOnlyDictionary<string, FolderFile> metas)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var path = prefix.Length == 0 ? folder.Name : prefix + "/" + folder.Name;
            if (metas.TryGetValue(path, out var meta))
            {
                folders[i] = folder with
                {
                    Variables = (meta.Variables ?? new List<KvDto>()).Select(KvDto.ToDomain).ToList(),
                    Headers = (meta.Headers ?? new List<KvDto>()).Select(KvDto.ToDomain).ToList(),
                    Auth = meta.Auth?.ToDomain() ?? folder.Auth,
                    PreRequestScript = meta.PreRequestScript,
                    TestsScript = meta.TestsScript,
                    Docs = meta.Docs,
                };
            }
            ApplyFolderMetadata(folders[i].Folders, path, metas);
        }
    }

    public static void Save(string root, Collection collection)
    {
        Directory.CreateDirectory(root);

        // Strip literal secret values into the encrypted sidecar so the .env.json files
        // written below never carry them. The in-memory collection is left untouched.
        var secretStore = new Persistence.EnvironmentSecretStore();
        if (collection.Environments.Count > 0)
        {
            var strippedEnvs = collection.Environments
                .Select(e => EnvironmentSecretSplitter.StripForPersistence(e, root, secretStore))
                .ToList();
            collection = collection with { Environments = strippedEnvs };
        }

        var (manifest, requests, environments) = CollectionJson.FromCollection(collection);

        File.WriteAllText(
            Path.Combine(root, CollectionJson.ManifestFileName),
            CollectionJson.SerializeManifest(manifest));

        // Group requests by folder path to determine which subdirectory each file goes in.
        foreach (var (req, folderPath) in requests)
        {
            var dir = string.IsNullOrEmpty(folderPath) ? root : Path.Combine(root, folderPath);
            Directory.CreateDirectory(dir);
            var fileName = Sanitize(req.Name) + CollectionJson.RequestSuffix;
            File.WriteAllText(Path.Combine(dir, fileName), CollectionJson.SerializeRequest(req));
        }

        // Per-folder metadata: write folder.json next to each folder's contents when the folder
        // carries any non-default field. Empty folders skip the file.
        WriteFolderMetadata(root, string.Empty, collection.Folders);

        if (environments.Count > 0)
        {
            var envDir = Path.Combine(root, CollectionJson.EnvironmentsFolder);
            Directory.CreateDirectory(envDir);
            foreach (var env in environments)
            {
                var fileName = Sanitize(env.Name) + CollectionJson.EnvironmentSuffix;
                File.WriteAllText(Path.Combine(envDir, fileName), CollectionJson.SerializeEnvironment(env));
            }
        }
    }

    private static void WriteFolderMetadata(string root, string prefix, IList<Folder> folders)
    {
        foreach (var folder in folders)
        {
            var path = prefix.Length == 0 ? folder.Name : prefix + "/" + folder.Name;
            if (HasFolderMetadata(folder))
            {
                var dir = Path.Combine(root, path);
                Directory.CreateDirectory(dir);
                var meta = new FolderFile
                {
                    Name = folder.Name,
                    Variables = folder.Variables.Count == 0 ? null : folder.Variables.Select(KvDto.FromDomain).ToList(),
                    Headers = folder.Headers.Count == 0 ? null : folder.Headers.Select(KvDto.FromDomain).ToList(),
                    Auth = folder.Auth is null ? null : AuthDto.FromDomain(folder.Auth),
                    PreRequestScript = string.IsNullOrEmpty(folder.PreRequestScript) ? null : folder.PreRequestScript,
                    TestsScript = string.IsNullOrEmpty(folder.TestsScript) ? null : folder.TestsScript,
                    Docs = string.IsNullOrEmpty(folder.Docs) ? null : folder.Docs,
                };
                File.WriteAllText(Path.Combine(dir, CollectionJson.FolderManifestFileName), CollectionJson.SerializeFolder(meta));
            }
            WriteFolderMetadata(root, path, folder.Folders);
        }
    }

    private static bool HasFolderMetadata(Folder f) =>
        f.Variables.Count > 0 || f.Headers.Count > 0 || f.Auth is not null ||
        !string.IsNullOrEmpty(f.PreRequestScript) ||
        !string.IsNullOrEmpty(f.TestsScript) ||
        !string.IsNullOrEmpty(f.Docs);

    private static string ToFolderPath(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        var dir = Path.GetDirectoryName(rel) ?? string.Empty;
        // Normalize separators to forward slashes for stable folder paths across OSes.
        return dir.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }
}
