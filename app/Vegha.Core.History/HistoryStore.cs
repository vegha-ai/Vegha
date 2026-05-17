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

        // Forward-compat: add request_blob column on older databases. ADD COLUMN IF NOT
        // EXISTS isn't supported; we probe via PRAGMA + tolerate the duplicate-column error.
        try { conn.Execute("ALTER TABLE history ADD COLUMN request_blob TEXT;"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already present */ }
    }

    public async Task<long> AppendAsync(
        string method,
        string url,
        int statusCode,
        long durationMs,
        string? responseBody,
        string? errorMessage,
        CancellationToken ct = default,
        string? requestBlob = null)
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
                INSERT INTO history (timestamp_utc, method, url, status_code, duration_ms, response_body_preview, error_message, request_blob)
                VALUES (@Ts, @Method, @Url, @Status, @Duration, @Preview, @Error, @Blob);
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
        CancellationToken ct = default)
        => GetRangeAsync(offset: 0, limit: limit, ct);

    /// <summary>Page-friendly slice of the most-recent entries. Ordered newest first;
    /// <paramref name="offset"/> skips that many rows from the head. The sidebar uses this for
    /// load-on-scroll paging.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetRangeAsync(
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) return Array.Empty<HistoryEntry>();

        using var conn = OpenConnection();
        var rows = await conn.QueryAsync(
            """
            SELECT id, timestamp_utc AS ts, method, url, status_code AS status,
                   duration_ms AS duration, response_body_preview AS preview, error_message AS error
              FROM history
             ORDER BY timestamp_utc DESC
             LIMIT @Limit OFFSET @Offset
            """,
            new { Limit = limit, Offset = offset }).ConfigureAwait(false);

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
        // Count prune: keep only the most recent MaxRetained rows.
        await conn.ExecuteAsync(
            """
            DELETE FROM history
             WHERE id IN (
                   SELECT id FROM history ORDER BY timestamp_utc DESC LIMIT -1 OFFSET @Keep
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

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            await conn.ExecuteAsync("DELETE FROM history;").ConfigureAwait(false);
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

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var n = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM history;").ConfigureAwait(false);
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
