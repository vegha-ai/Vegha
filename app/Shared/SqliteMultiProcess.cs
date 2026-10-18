using Microsoft.Data.Sqlite;

namespace Vegha.Core.Shared;

/// <summary>
/// Connection settings for the SQLite stores under <c>%LocalAppData%/Vegha</c> that the
/// desktop app, the CLI and the VS Code sidecar open concurrently from separate processes.
/// Linked into each store's assembly (no shared low-level project exists for it).
///
/// <list type="bullet">
///   <item>Private cache — shared-cache mode reports <c>SQLITE_LOCKED</c> for table-level
///     conflicts, which the native busy handler never retries.</item>
///   <item>WAL — readers in one process don't block a writer in another. The journal mode
///     is persistent in the database file, so it is set once in <see cref="EnableWal"/>.</item>
///   <item><c>busy_timeout</c> — a writer waits for the other process's transaction instead
///     of failing immediately with <c>SQLITE_BUSY</c>.</item>
/// </list>
/// </summary>
internal static class SqliteMultiProcess
{
    /// <summary>How long a connection waits on another process's lock before giving up.</summary>
    public const int BusyTimeoutMs = 5000;

    public static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = BusyTimeoutMs / 1000,
        }.ToString();

    /// <summary>Opens a connection with the per-connection pragmas applied.</summary>
    public static SqliteConnection Open(string connectionString)
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Switches the database to WAL. Persistent, so call once from schema setup.
    /// Tolerates failure (e.g. a filesystem without shared-memory support) — the store
    /// still works in the default rollback-journal mode.</summary>
    public static void EnableWal(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteScalar();
        }
        catch (SqliteException) { /* stay on the rollback journal */ }
    }
}
