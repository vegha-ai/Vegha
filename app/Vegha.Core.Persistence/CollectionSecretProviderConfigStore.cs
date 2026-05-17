namespace Vegha.Core.Persistence;

/// <summary>
/// Factory + migration helper that scopes a <see cref="SecretProviderConfigStore"/> to a
/// single collection. Provider configs land in <c>&lt;collection&gt;/.secrets/providers.json</c>
/// (encrypted) with the matching <c>providers.key</c> alongside. The <c>.secrets/</c> folder
/// should be in the collection's .gitignore so credentials aren't committed.
/// </summary>
public static class CollectionSecretProviderConfigStore
{
    public const string FolderName = ".secrets";
    private const string MigratedMarkerFile = ".migrated";

    /// <summary>Resolves the per-collection secrets directory.</summary>
    public static string DirectoryFor(string collectionFolder) =>
        Path.Combine(collectionFolder, FolderName);

    /// <summary>Returns a store rooted at <c>&lt;collectionFolder&gt;/.secrets/</c>.</summary>
    public static SecretProviderConfigStore Create(string collectionFolder) =>
        new SecretProviderConfigStore(DirectoryFor(collectionFolder));

    /// <summary>One-shot copy of the legacy app-global provider list into a freshly-activated
    /// collection. Safe to call on every activation: writes the migration marker on success and
    /// is a no-op if the marker already exists or the global store is empty.</summary>
    public static void MigrateGlobalIfNeeded(string collectionFolder, SecretProviderConfigStore? global = null)
    {
        try
        {
            var dir = DirectoryFor(collectionFolder);
            var marker = Path.Combine(dir, MigratedMarkerFile);
            if (File.Exists(marker)) return;

            var existing = Path.Combine(dir, "providers.json");
            if (File.Exists(existing))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                return;
            }

            global ??= new SecretProviderConfigStore();
            var configs = global.Load();
            if (configs.Count == 0)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                return;
            }

            var perCollection = Create(collectionFolder);
            perCollection.Save(configs);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Migration is best-effort — a missing copy doesn't break the panel, the user
            // can re-add providers manually.
        }
    }
}
