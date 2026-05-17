using System.IO;

namespace Vegha.Core.Persistence;

/// <summary>
/// Ensures a usable workspace exists on first launch. Creates the default workspace at
/// <c>%AppData%/Roaming/Vegha/default-workspace</c> (per spec — this is distinct
/// from the Local config dir at <c>%LocalAppData%/Vegha</c>) and registers it in
/// the <see cref="WorkspaceStore"/>. Idempotent: safe to call on every startup.
/// </summary>
public static class WorkspaceBootstrapper
{
    /// <summary>Folder name under <c>%AppData%/Roaming/Vegha/</c>. Bruno uses
    /// <c>~/Documents/bruno</c>; we substitute Roaming AppData per project spec.</summary>
    public const string DefaultWorkspaceFolderName = "default-workspace";

    public const string DefaultWorkspaceDisplayName = "Default Workspace";

    /// <summary>Returns the absolute path of the default workspace folder.</summary>
    public static string GetDefaultWorkspaceFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vegha",
            DefaultWorkspaceFolderName);

    /// <summary>If no default-flagged workspace is registered, creates the folder, writes
    /// <c>workspace.yml</c>, and registers it (activating if there's no other active one).
    /// Returns the resolved <see cref="Workspace"/>.</summary>
    public static Workspace EnsureDefaultWorkspace(WorkspaceStore store, string? folderOverride = null)
    {
        var state = store.Load();
        var folder = folderOverride ?? GetDefaultWorkspaceFolder();

        // Already registered? Just make sure the folder + manifest exist.
        var existing = state.Workspaces.FirstOrDefault(w => w.IsDefault)
                    ?? state.Workspaces.FirstOrDefault(w =>
                           string.Equals(NormalizePath(w.FolderPath), NormalizePath(folder),
                                         StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            EnsureFolderLayout(existing.FolderPath, existing.Name);
            // If the registered entry isn't flagged default yet, upgrade it.
            if (!existing.IsDefault)
            {
                var idx = state.Workspaces.IndexOf(existing);
                var upgraded = existing with { IsDefault = true };
                var list = state.Workspaces.ToList();
                list[idx] = upgraded;
                store.Save(state with { Workspaces = list });
                return upgraded;
            }
            return existing;
        }

        // First-time creation.
        EnsureFolderLayout(folder, DefaultWorkspaceDisplayName);
        var ws = new Workspace(DefaultWorkspaceDisplayName, folder) { IsDefault = true };
        var newWorkspaces = state.Workspaces.ToList();
        newWorkspaces.Add(ws);
        var newActive = state.ActiveIndex < 0 ? newWorkspaces.Count - 1 : state.ActiveIndex;
        store.Save(state with { Workspaces = newWorkspaces, ActiveIndex = newActive });
        return ws;
    }

    /// <summary>Creates <c>folder/</c>, <c>folder/collections/</c>, <c>folder/environments/</c>,
    /// <c>folder/scripts/</c> and writes a <c>workspace.yml</c> if missing. Used by both the
    /// bootstrapper and the Create-workspace dialog. The <c>scripts/</c> folder hosts the
    /// optional workspace-level <c>pre-request.js</c> and <c>tests.js</c> that merge into
    /// every collection's execution chain.</summary>
    public static void EnsureFolderLayout(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "collections"));
        Directory.CreateDirectory(Path.Combine(folder, "environments"));
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        if (!WorkspaceManifestIO.Exists(folder))
        {
            WorkspaceManifestIO.Write(folder, new WorkspaceManifest
            {
                Version = 1,
                Name = name,
                Created = DateTimeOffset.UtcNow,
            });
        }
    }

    /// <summary>Writes a starter <c>.gitignore</c> at the collection root so per-collection
    /// secrets (encrypted but the AES key sits in the same folder) and editor noise don't get
    /// committed. Idempotent — appends missing entries when a .gitignore already exists.</summary>
    public static void EnsureCollectionGitIgnore(string collectionFolder)
    {
        try
        {
            if (string.IsNullOrEmpty(collectionFolder) || !Directory.Exists(collectionFolder)) return;
            var gitIgnorePath = Path.Combine(collectionFolder, ".gitignore");
            var required = new[] { ".secrets/", ".secrets/*" };
            if (!File.Exists(gitIgnorePath))
            {
                File.WriteAllText(gitIgnorePath, string.Join('\n', required) + '\n');
                return;
            }
            var existing = File.ReadAllText(gitIgnorePath);
            var missing = required.Where(line =>
                !existing.Split('\n').Any(l => l.Trim().Equals(line, StringComparison.Ordinal))).ToList();
            if (missing.Count == 0) return;
            File.AppendAllText(gitIgnorePath,
                (existing.EndsWith('\n') ? string.Empty : "\n") + string.Join('\n', missing) + '\n');
        }
        catch { /* best-effort — missing .gitignore won't break the app */ }
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }
}
