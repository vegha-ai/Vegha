using Dapper;
using Microsoft.Data.Sqlite;

namespace Vegha.Core.Persistence;

/// <summary>One persisted open tab, including its full working state. <see cref="StateBlob"/> is a
/// JSON snapshot of the editor (method, url, headers, body, auth, scripts, …) so unsaved/dirty
/// edits and untitled scratch requests survive a workspace switch or an app restart. The blob is
/// only populated for tabs that need it (dirty edits, scratch drafts); a clean, file-backed tab is
/// reconstructed from its <see cref="SourcePath"/> on disk instead.</summary>
public sealed record TabStateRow(
    string Id,
    string? WorkspaceId,
    string? CollectionPath,
    string? SourcePath,
    string Name,
    string Kind,
    int OrderIndex,
    bool IsActive,
    bool IsDirty,
    bool IsScratch,
    string? StateBlob);

/// <summary>SQLite-backed store for the open-tabs session, including full editor state.
/// Default path <c>%LocalAppData%/Vegha/tabs.db</c>. The whole set is rewritten on each
/// <see cref="SaveAll"/> (the open-tab set is small), so callers snapshot at checkpoints —
/// collection switch, workspace switch, app close — rather than on every keystroke.</summary>
public sealed class TabStateStore
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public TabStateStore() : this(DefaultDatabasePath()) { }

    public TabStateStore(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        EnsureSchema();
    }

    public static string DefaultDatabasePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha",
            "tabs.db");

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS open_tabs (
                id               TEXT PRIMARY KEY,
                workspace_id     TEXT,
                collection_path  TEXT,
                source_path      TEXT,
                name             TEXT NOT NULL,
                kind             TEXT NOT NULL,
                order_index      INTEGER NOT NULL,
                is_active        INTEGER NOT NULL,
                is_dirty         INTEGER NOT NULL,
                is_scratch       INTEGER NOT NULL,
                state_blob       TEXT
            );
            """);
    }

    /// <summary>Replaces the entire persisted set with <paramref name="rows"/> in one transaction.</summary>
    public void SaveAll(IReadOnlyList<TabStateRow> rows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM open_tabs;", transaction: tx);
        if (rows.Count > 0)
        {
            conn.Execute(
                """
                INSERT INTO open_tabs
                    (id, workspace_id, collection_path, source_path, name, kind,
                     order_index, is_active, is_dirty, is_scratch, state_blob)
                VALUES
                    (@Id, @WorkspaceId, @CollectionPath, @SourcePath, @Name, @Kind,
                     @OrderIndex, @IsActive, @IsDirty, @IsScratch, @StateBlob);
                """,
                rows.Select(r => new
                {
                    r.Id,
                    r.WorkspaceId,
                    r.CollectionPath,
                    r.SourcePath,
                    r.Name,
                    r.Kind,
                    r.OrderIndex,
                    IsActive = r.IsActive ? 1 : 0,
                    IsDirty = r.IsDirty ? 1 : 0,
                    IsScratch = r.IsScratch ? 1 : 0,
                    r.StateBlob,
                }),
                transaction: tx);
        }
        tx.Commit();
    }

    /// <summary>Loads all persisted tabs, ordered by their saved position.</summary>
    public IReadOnlyList<TabStateRow> LoadAll()
    {
        try
        {
            using var conn = OpenConnection();
            // Read INTEGER columns as long and convert to bool ourselves — Dapper's positional-
            // record mapping won't coerce long→bool through the constructor.
            var raw = conn.Query<RawRow>("""
                SELECT id AS Id, workspace_id AS WorkspaceId, collection_path AS CollectionPath,
                       source_path AS SourcePath, name AS Name, kind AS Kind,
                       order_index AS OrderIndex, is_active AS IsActive, is_dirty AS IsDirty,
                       is_scratch AS IsScratch, state_blob AS StateBlob
                FROM open_tabs
                ORDER BY order_index ASC;
                """);
            return raw.Select(r => new TabStateRow(
                r.Id, r.WorkspaceId, r.CollectionPath, r.SourcePath, r.Name, r.Kind,
                (int)r.OrderIndex, r.IsActive != 0, r.IsDirty != 0, r.IsScratch != 0, r.StateBlob)).ToList();
        }
        catch
        {
            return Array.Empty<TabStateRow>();
        }
    }

    private sealed class RawRow
    {
        public string Id { get; set; } = string.Empty;
        public string? WorkspaceId { get; set; }
        public string? CollectionPath { get; set; }
        public string? SourcePath { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long OrderIndex { get; set; }
        public long IsActive { get; set; }
        public long IsDirty { get; set; }
        public long IsScratch { get; set; }
        public string? StateBlob { get; set; }
    }
}
