using System.Text.RegularExpressions;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.Importers;

/// <summary>
/// Loads a Bruno environment file (<c>environments/*.bru</c>). Bruno env files use the standard
/// <c>vars { ... }</c> dict block, plus an extended <c>vars:secret [ ... ]</c> block-level list
/// of variable names (comma-separated, not standard grammar). This loader extracts each block
/// independently so env files round-trip cleanly even with the block-level list syntax.
/// </summary>
public static class EnvironmentLoader
{
    private static readonly Regex VarsSecretBlock =
        new(@"vars:secret\s*\[(?<body>.*?)\]", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Loads a single env file. Returns null on unrecoverable parse failure.</summary>
    public static DomainEnv? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var name = Path.GetFileNameWithoutExtension(filePath);
        var raw = File.ReadAllText(filePath);

        var secrets = ExtractSecretNames(raw);

        // Strip the vars:secret block before handing to the strict parser so it doesn't choke on
        // the [ ... ] block-level list (not in standard Bruno grammar).
        var stripped = VarsSecretBlock.Replace(raw, string.Empty);

        var vars = new List<KvPair>();
        if (BruParser.TryParse(stripped, out var doc, out _))
        {
            var varsBlock = doc.Blocks.OfType<DictBlock>().FirstOrDefault(b => b.Name == "vars");
            if (varsBlock is not null)
            {
                foreach (var p in varsBlock.Pairs)
                {
                    vars.Add(new KvPair(
                        p.Name,
                        p.Value is StringValue s ? s.Text : string.Empty,
                        p.Enabled));
                }
            }
        }

        return new DomainEnv
        {
            // Bruno .bru env files don't carry an id field — derive one deterministically
            // from the absolute file path so the same env produces the same Id across loads
            // (a fresh Guid each time would invalidate the workspace's persisted
            // ActiveGlobalEnvironmentId on every restart).
            Id = "bru:" + ShortHash(filePath),
            Name = name,
            Variables = vars,
            SecretVariables = secrets,
        };
    }

    private static string ShortHash(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    /// <summary>Loads every env file under <paramref name="environmentsDir"/>. Reads both the
    /// legacy Bruno <c>.bru</c> format and the JSON-native <c>.env.json</c> format the
    /// in-app importer and panel write. Empty list if the directory is missing. When the
    /// same env name appears in both formats, <c>.env.json</c> wins (it round-trips
    /// metadata like <c>Color</c> that the Bruno format doesn't carry).</summary>
    public static List<DomainEnv> LoadDirectory(string environmentsDir)
    {
        var list = new List<DomainEnv>();
        if (!Directory.Exists(environmentsDir)) return list;

        // The collection root hosts the encrypted secret sidecar (.secrets/). Used below to
        // merge literal secret values back into .env.json envs that were stripped on save.
        var collectionRoot = Directory.GetParent(environmentsDir)?.FullName;
        var secretStore = new Vegha.Core.Persistence.EnvironmentSecretStore();

        // .bru envs (legacy).
        foreach (var file in Directory.EnumerateFiles(environmentsDir, "*.bru", SearchOption.TopDirectoryOnly)
                                       .OrderBy(f => f, StringComparer.Ordinal))
        {
            var env = Load(file);
            if (env is not null) list.Add(env);
        }

        // .env.json envs (canonical format — what the Import button and the env panel write).
        // Without this branch, importing a Postman env or creating one through the UI would
        // succeed in memory but vanish on the next reload because CollectionLoader rebuilds
        // the in-memory Environments list from disk and the file wasn't being read back.
        foreach (var file in Directory.EnumerateFiles(environmentsDir, "*.env.json", SearchOption.TopDirectoryOnly)
                                       .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var json = File.ReadAllText(file);
                var dto = CollectionJson.DeserializeEnvironment(json);
                if (dto is null) continue;
                var env = EnvironmentFile.ToDomain(dto);
                // Restore literal secret values from the encrypted sidecar (or
                // VEGHA_SECRET_* overrides) — they were stripped out of the .env.json on save.
                if (collectionRoot is not null)
                    env = EnvironmentSecretSplitter.MergeFromStore(env, collectionRoot, secretStore);
                // .env.json wins over a same-named .bru entry — strip the prior one if present.
                var existing = list.FindIndex(e => string.Equals(e.Name, env.Name, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) list[existing] = env;
                else list.Add(env);
            }
            catch
            {
                // Malformed .env.json shouldn't break the rest of the load; skip it.
            }
        }

        return list;
    }

    private static List<string> ExtractSecretNames(string raw)
    {
        var match = VarsSecretBlock.Match(raw);
        if (!match.Success) return new List<string>();

        return match.Groups["body"].Value
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
