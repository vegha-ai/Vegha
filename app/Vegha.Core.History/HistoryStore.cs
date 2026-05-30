using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Vegha.Core.History;

/// <summary>
/// SQLite-backed history store. Default path: <c>%LocalAppData%/Vegha/history.db</c>.
/// Stores up to <see cref="MaxRetained"/> entries; oldest are pruned on insert.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    /// <summary>Default count cap when no setting has overridden it.</summary>
    public const int DefaultMaxRetained = 1000;

    /// <summary>Default cap for response body previews. The instance-level
    /// <see cref="MaxPreviewChars"/> overrides this and is wired to AppSettings.MaxBodySizeMb.</summary>
    public const int DefaultPreviewMaxChars = 4000;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>When false, <see cref="AppendAsync"/> still records the request row
    /// (method/url/status/duration/timestamp/error) but drops the response body preview
    /// and request blob so no payload data is persisted. Wired from
    /// AppSettings.SaveResponsesToHistory by the App layer. Toggling does not delete
    /// already-stored entries.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-row preview truncation length. Defaults to <see cref="DefaultPreviewMaxChars"/>;
    /// the App layer raises this from AppSettings.MaxBodySizeMb (converted to chars).</summary>
    public int MaxPreviewChars { get; set; } = DefaultPreviewMaxChars;

    /// <summary>Count cap. Rows beyond this are pruned (oldest first) on every insert.
    /// Wired from AppSettings.HistoryRetentionMaxEntries by the App layer.</summary>
    public int MaxRetained { get; set; } = DefaultMaxRetained;

    /// <summary>Age cap. Rows older than this are pruned on every insert. <see cref="TimeSpan.Zero"/>
    /// disables age pruning. Wired from AppSettings.HistoryRetentionDays by the App layer.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(365);

    public string DatabasePath { get; }

    public HistoryStore() : this(DefaultDatabasePath()) { }

    public HistoryStore(string databasePath)
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
            "history.db");

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS history (
                id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc            INTEGER NOT NULL,
                method                   TEXT NOT NULL,
                url                      TEXT NOT NULL,
                status_code              INTEGER NOT NULL,
                duration_ms              INTEGER NOT NULL,
                response_body_preview    TEXT,
                error_message            TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_history_ts ON history (timestamp_utc DESC);
            """);

        // Forward-compat column adds on older databases. ADD COLUMN IF NOT EXISTS isn't
        // supported, so each add tolerates the duplicate-column error when already present.
        //   • request_blob  — full request snapshot for History → Replay (see RequestEditorViewModel).
        //   • workspace_id  — folder path of the owning workspace so history is per-workspace.
        try { conn.Execute("ALTER TABLE history ADD COLUMN request_blob TEXT;"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already present */ }
        try { conn.Execute("ALTER TABLE history ADD COLUMN workspace_id TEXT;"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already present */ }

        // Per-workspace paging index. Created after the ALTER so the column exists.
        try { conn.Execute("CREATE INDEX IF NOT EXISTS idx_history_ws ON history (workspace_id, timestamp_utc DESC);"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* tolerate older engines */ }
    }

    public async Task<long> AppendAsync(
        string method,
        string url,
        int statusCode,
        long durationMs,
        string? responseBody,
        string? errorMessage,
        CancellationToken ct = default,
        string? requestBlob = null,
        string? workspaceId = null)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Body + request blob are gated by Enabled; the row itself is always recorded so
            // the activity list stays useful even when payload persistence is off for compliance.
            var persistPayloads = Enabled;
            using var conn = OpenConnection();
            var id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO history (timestamp_utc, method, url, status_code, duration_ms, response_body_preview, error_message, request_blob, workspace_id)
                VALUES (@Ts, @Method, @Url, @Status, @Duration, @Preview, @Error, @Blob, @Workspace);
                SELECT last_insert_rowid();
                """,
                new
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Method = method ?? string.Empty,
                    Url = url ?? string.Empty,
                    Status = statusCode,
                    Duration = durationMs,
                    Preview = persistPayloads ? Truncate(responseBody, MaxPreviewChars) : null,
                    Error = errorMessage,
                    Blob = persistPayloads ? requestBlob : null,
                    Workspace = string.IsNullOrEmpty(workspaceId) ? null : workspaceId,
                }).ConfigureAwait(false);

            await PruneInternalAsync(conn).ConfigureAwait(false);
            return id;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(
        int limit = 100,
        string? workspaceId = null,
        CancellationToken ct = default)
        => GetRangeAsync(offset: 0, limit: limit, workspaceId: workspaceId, search: null, ct: ct);

    /// <summary>Page-friendly slice of the most-recent entries. Ordered newest first;
    /// <paramref name="offset"/> skips that many rows from the head. The sidebar uses this for
    /// load-on-scroll paging.
    ///
    /// <paramref name="workspaceId"/> scopes the slice to one workspace (its folder path); null
    /// returns every workspace's rows (used by tests and the legacy "all" view).
    /// <paramref name="search"/> filters by a case-insensitive substring of method or URL.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetRangeAsync(
        int offset,
        int limit,
        string? workspaceId = null,
        string? search = null,
        CancellationToken ct = default)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) return Array.Empty<HistoryEntry>();

        var (whereSql, prms) = BuildFilter(workspaceId, search);
        prms.Add("Limit", limit);
        prms.Add("Offset", offset);

        using var conn = OpenConnection();
        var rows = await conn.QueryAsync(
            $"""
            SELECT id, timestamp_utc AS ts, method, url, status_code AS status,
                   duration_ms AS duration, response_body_preview AS preview, error_message AS error
              FROM history
             {whereSql}
             ORDER BY timestamp_utc DESC, id DESC
             LIMIT @Limit OFFSET @Offset
            """,
            prms).ConfigureAwait(false);

        return rows.Select(r => new HistoryEntry(
            Id: (long)r.id,
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds((long)r.ts),
            Method: (string)r.method,
            Url: (string)r.url,
            StatusCode: (int)(long)r.status,
            DurationMs: (long)r.duration,
            ResponseBodyPreview: r.preview as string,
            ErrorMessage: r.error as string)).ToList();
    }

    /// <summary>Builds the <c>WHERE</c> clause + Dapper parameters shared by the read/count/clear
    /// paths. A null/empty <paramref name="workspaceId"/> applies no workspace filter (all rows);
    /// a non-empty value matches that workspace exactly. A non-blank <paramref name="search"/>
    /// adds a case-insensitive substring match against method or URL.</summary>
    private static (string Sql, DynamicParameters Parameters) BuildFilter(string? workspaceId, string? search)
    {
        var clauses = new List<string>();
        var prms = new DynamicParameters();

        if (!string.IsNullOrEmpty(workspaceId))
        {
            clauses.Add("workspace_id = @Workspace");
            prms.Add("Workspace", workspaceId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Escape LIKE wildcards so a literal % or _ in the query matches literally.
            var term = search.Trim()
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            clauses.Add("(url LIKE @Search ESCAPE '\\' OR method LIKE @Search ESCAPE '\\')");
            prms.Add("Search", "%" + term + "%");
        }

        var sql = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        return (sql, prms);
    }

    /// <summary>Runs the count + age prunes once. Called at startup so accumulated rows that
    /// fall outside the current retention policy are dropped even when the user never inserts
    /// a new row this session.</summary>
    public async Task PruneAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            await PruneInternalAsync(conn).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task PruneInternalAsync(IDbConnection conn)
    {
        // Count prune: keep only the most recent MaxRetained rows PER WORKSPACE so a busy
        // workspace can't evict another workspace's history. Rows with no workspace_id
        // (legacy/global, pre-migration) share a single partition. ROW_NUMBER requires a
        // window-function-capable SQLite (3.25+, which the bundled engine satisfies).
        await conn.ExecuteAsync(
            """
            DELETE FROM history
             WHERE id IN (
                   SELECT id FROM (
                       SELECT id,
                              ROW_NUMBER() OVER (
                                  PARTITION BY COALESCE(workspace_id, '')
                                  ORDER BY timestamp_utc DESC, id DESC
                              ) AS rn
                         FROM history
                   ) WHERE rn > @Keep
             );
            """,
            new { Keep = Math.Max(1, MaxRetained) }).ConfigureAwait(false);

        // Age prune: drop anything older than the cutoff. Skipped when MaxAge is Zero so the
        // user can opt out via settings.
        if (MaxAge > TimeSpan.Zero)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(MaxAge).ToUnixTimeMilliseconds();
            await conn.ExecuteAsync(
                "DELETE FROM history WHERE timestamp_utc < @Cutoff;",
                new { Cutoff = cutoff }).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes history rows. A null/empty <paramref name="workspaceId"/> clears every
    /// workspace; a non-empty value clears only that workspace's rows (the History panel passes
    /// the active workspace so "Clear" is scoped to what the user is looking at).</summary>
    public async Task ClearAsync(string? workspaceId = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            if (string.IsNullOrEmpty(workspaceId))
                await conn.ExecuteAsync("DELETE FROM history;").ConfigureAwait(false);
            else
                await conn.ExecuteAsync(
                    "DELETE FROM history WHERE workspace_id = @Workspace;",
                    new { Workspace = workspaceId }).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>One-time migration: assigns every row that has no workspace (legacy/global
    /// history recorded before per-workspace scoping) to <paramref name="workspaceId"/>. Called
    /// at startup with the active workspace so a user's existing history lands in the workspace
    /// they were last using rather than vanishing under the new per-workspace filter. Idempotent
    /// — once every row has a workspace it matches nothing.</summary>
    public async Task<int> BackfillWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workspaceId)) return 0;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            return await conn.ExecuteAsync(
                "UPDATE history SET workspace_id = @Workspace WHERE workspace_id IS NULL;",
                new { Workspace = workspaceId }).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            await conn.ExecuteAsync(
                "DELETE FROM history WHERE id = @Id",
                new { Id = id }).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountAsync(string? workspaceId = null, CancellationToken ct = default)
    {
        var (whereSql, prms) = BuildFilter(workspaceId, search: null);
        using var conn = OpenConnection();
        var n = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM history {whereSql};", prms).ConfigureAwait(false);
        return (int)n;
    }

    /// <summary>Fetches a single history entry by id, including its (truncated) body preview.</summary>
    public async Task<HistoryEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var r = await conn.QueryFirstOrDefaultAsync(
            """
            SELECT id, timestamp_utc AS ts, method, url, status_code AS status,
                   duration_ms AS duration, response_body_preview AS preview, error_message AS error
              FROM history WHERE id = @Id
            """,
            new { Id = id }).ConfigureAwait(false);
        if (r is null) return null;
        return new HistoryEntry(
            Id: (long)r.id,
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds((long)r.ts),
            Method: (string)r.method,
            Url: (string)r.url,
            StatusCode: (int)(long)r.status,
            DurationMs: (long)r.duration,
            ResponseBodyPreview: r.preview as string,
            ErrorMessage: r.error as string);
    }

    /// <summary>Returns the persisted request_blob for the given entry, or null when the
    /// row predates the column or no blob was attached. The blob is opaque to the store —
    /// callers serialize whatever shape they need to round-trip.</summary>
    public async Task<string?> GetRequestBlobAsync(long id, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        try
        {
            return await conn.ExecuteScalarAsync<string?>(
                "SELECT request_blob FROM history WHERE id = @Id",
                new { Id = id }).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;  // very old DB without the column
        }
    }

    public void Dispose() => _writeLock.Dispose();

    // ============================== helpers ==============================

    private IDbConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string? Truncate(string? s, int max)
    {
        if (s is null) return null;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
