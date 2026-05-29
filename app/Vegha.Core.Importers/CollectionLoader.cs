using System.Collections.Concurrent;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Loads a Bruno-style collection from a directory tree on disk. Each <c>.bru</c> file becomes
/// a <see cref="RequestItem"/>; subdirectories become nested <see cref="Folder"/>s. The optional
/// <c>collection.bru</c> at the root supplies the collection name (from its meta block).
/// </summary>
public static class CollectionLoader
{
    /// <summary>Folder names skipped during traversal.</summary>
    public static readonly HashSet<string> IgnoredFolders =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".apitest", ".bruno", "node_modules", "bin", "obj" };

    /// <summary>Folder names treated as side-channel data, not request folders.</summary>
    public static readonly HashSet<string> ReservedFolders =
        new(StringComparer.OrdinalIgnoreCase) { "environments" };

    /// <summary>Loads a collection from <paramref name="rootDirectory"/>. Throws if the directory does not exist.</summary>
    public static Collection Load(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"Collection directory not found: {rootDirectory}");

        var collectionMeta = TryParseNodeBru(Path.Combine(rootDirectory, "collection.bru"));
        var name = collectionMeta?.Name ?? Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var (requests, folders) = LoadFolderContents(rootDirectory);
        var environments = EnvironmentLoader.LoadDirectory(Path.Combine(rootDirectory, "environments"));

        return new Collection
        {
            Name = name,
            Requests = requests,
            Folders = folders,
            Environments = environments,
            Auth = collectionMeta?.Auth,
            Headers = collectionMeta?.Headers ?? new List<KvPair>(),
            Variables = collectionMeta?.Variables ?? new List<KvPair>(),
            PreRequestScript = collectionMeta?.PreRequestScript,
            PostResponseScript = collectionMeta?.PostResponseScript,
            TestsScript = collectionMeta?.TestsScript,
            Docs = collectionMeta?.Docs,
        };
    }

    private static (List<RequestItem> Requests, List<Folder> Folders) LoadFolderContents(string dir)
    {
        // BruParser.Parse and TryLoadRequest are pure (fresh Scanner per call, no shared
        // mutable state), so File.ReadAllText + parse can fan out across cores. Sort order
        // is restored below so output is deterministic regardless of parse-completion order.

        var requestFiles = Directory.EnumerateFiles(dir, "*.bru", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return !string.Equals(n, "collection.bru", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(n, "folder.bru", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(n, "environment.bru", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var requestBag = new ConcurrentBag<RequestItem>();
        Parallel.ForEach(requestFiles, file =>
        {
            var req = TryLoadRequest(file);
            if (req is not null) requestBag.Add(req);
        });

        var requests = requestBag.ToList();
        requests.Sort((a, b) =>
        {
            var bySeq = a.Sequence.CompareTo(b.Sequence);
            return bySeq != 0 ? bySeq : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        var subDirs = Directory.EnumerateDirectories(dir)
            .Where(d =>
            {
                var n = Path.GetFileName(d);
                return !IgnoredFolders.Contains(n) && !ReservedFolders.Contains(n);
            })
            .ToArray();

        // Tuple keeps the original disk-path key alongside the Folder so we can restore the
        // pre-parallelization order (OrderBy on subDir StringComparer.Ordinal) — important
        // when folder.bru overrides the display name and would otherwise reshuffle results.
        var folderBag = new ConcurrentBag<(string SortKey, Folder Folder)>();
        Parallel.ForEach(subDirs, subDir =>
        {
            var subName = Path.GetFileName(subDir);
            var (subRequests, subFolders) = LoadFolderContents(subDir);
            // Skip implicit empty folders (no requests, no nested content, no explicit marker)
            // to avoid surfacing junk directories. An explicit folder.bru keeps a freshly-
            // created (but still empty) folder visible — without that exception, "New Folder"
            // appears to do nothing because the loader drops the brand-new directory.
            var folderBruPath = Path.Combine(subDir, "folder.bru");
            var hasFolderBru = File.Exists(folderBruPath);
            if (subRequests.Count == 0 && subFolders.Count == 0 && !hasFolderBru) return;

            var folderMeta = TryParseNodeBru(folderBruPath);
            folderBag.Add((subDir, new Folder
            {
                Name = folderMeta?.Name ?? subName,
                Requests = subRequests,
                Folders = subFolders,
                Auth = folderMeta?.Auth,
                Headers = folderMeta?.Headers ?? new List<KvPair>(),
                Variables = folderMeta?.Variables ?? new List<KvPair>(),
                PreRequestScript = folderMeta?.PreRequestScript,
                PostResponseScript = folderMeta?.PostResponseScript,
                TestsScript = folderMeta?.TestsScript,
                Docs = folderMeta?.Docs,
            }));
        });

        var folders = folderBag
            .OrderBy(t => t.SortKey, StringComparer.Ordinal)
            .Select(t => t.Folder)
            .ToList();

        return (requests, folders);
    }

    private static RequestItem? TryLoadRequest(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var doc = BruParser.Parse(text);
            var req = BruToRequestConverter.Convert(doc);
            // Stamp the on-disk path so the UI tree builder can resolve the file without a
            // second parse pass. Fall back to file name (without .bru) when meta.name is missing.
            req = req with
            {
                SourcePath = filePath,
                Name = string.IsNullOrEmpty(req.Name) ? Path.GetFileNameWithoutExtension(filePath) : req.Name,
            };
            return req;
        }
        catch
        {
            // Malformed .bru shouldn't crash the load — surface via a future "load issues" panel.
            return null;
        }
    }

    /// <summary>Reads <c>collection.bru</c> or <c>folder.bru</c> into a <see cref="NodeMeta"/>.
    /// Pulls the full inheritance surface — headers / vars / auth / scripts / docs — so the
    /// Properties dialog can edit and save back round-trips. Returns null when the file
    /// doesn't exist or fails to parse.</summary>
    private static NodeMeta? TryParseNodeBru(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var doc = BruParser.Parse(File.ReadAllText(path));
            return ParseNodeMeta(doc);
        }
        catch
        {
            return null;
        }
    }

    private static NodeMeta ParseNodeMeta(BruDocument doc)
    {
        string? name = null;
        var meta = doc.Blocks.OfType<DictBlock>().FirstOrDefault(b => b.Name == "meta");
        if (meta is not null)
            name = (meta.Pairs.FirstOrDefault(p => p.Name == "name")?.Value as StringValue)?.Text;

        var headers = ReadKvBlock(doc, "headers");
        var vars = ReadKvBlock(doc, "vars");
        var preRequest = ReadTextBlock(doc, "script:pre-request");
        var postResponse = ReadTextBlock(doc, "script:post-response");
        var tests = ReadTextBlock(doc, "tests");
        var docsText = ReadTextBlock(doc, "docs");
        var auth = ReadAuthBlock(doc);

        return new NodeMeta(name, auth, headers, vars, preRequest, postResponse, tests, docsText);
    }

    private static List<KvPair> ReadKvBlock(BruDocument doc, string blockName)
    {
        var block = doc.Blocks.OfType<DictBlock>().FirstOrDefault(b => b.Name == blockName);
        var result = new List<KvPair>();
        if (block is null) return result;
        foreach (var p in block.Pairs)
        {
            var value = p.Value switch
            {
                StringValue s => s.Text,
                _ => string.Empty,
            };
            result.Add(new KvPair(p.Name, value, p.Enabled));
        }
        return result;
    }

    private static string? ReadTextBlock(BruDocument doc, string blockName)
    {
        var block = doc.Blocks.OfType<TextBlock>().FirstOrDefault(b => b.Name == blockName);
        // Trim trailing newline/whitespace introduced by the closing brace of the bru block.
        // Leading whitespace matters for code blocks (preserve indentation) so we only Trim end.
        return block?.Text.TrimEnd();
    }

    private static AuthConfig? ReadAuthBlock(BruDocument doc)
    {
        // Look for any auth:* dict block — the first one wins (a node has at most one auth).
        foreach (var block in doc.Blocks.OfType<DictBlock>())
        {
            if (!block.Name.StartsWith("auth:", StringComparison.OrdinalIgnoreCase)) continue;
            var typeName = block.Name["auth:".Length..];
            var type = ParseAuthType(typeName);
            if (type is null) continue;
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in block.Pairs)
                if (p.Value is StringValue s) parameters[p.Name] = s.Text;
            return new AuthConfig { Type = type.Value, Parameters = parameters };
        }
        return null;
    }

    private static AuthType? ParseAuthType(string name) => name.ToLowerInvariant() switch
    {
        "apikey" => AuthType.ApiKey,
        "bearer" => AuthType.Bearer,
        "basic" => AuthType.Basic,
        "digest" => AuthType.Digest,
        "oauth1" => AuthType.OAuth1,
        "oauth2" => AuthType.OAuth2,
        "awsv4" => AuthType.AwsV4,
        "ntlm" => AuthType.Ntlm,
        "wsse" => AuthType.Wsse,
        _ => null,
    };

    private sealed record NodeMeta(
        string? Name,
        AuthConfig? Auth,
        List<KvPair> Headers,
        List<KvPair> Variables,
        string? PreRequestScript,
        string? PostResponseScript,
        string? TestsScript,
        string? Docs);
}
