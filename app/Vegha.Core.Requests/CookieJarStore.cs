using System.Net;
using Dapper;
using Microsoft.Data.Sqlite;
using Vegha.Core.Shared;

namespace Vegha.Core.Requests;

/// <summary>
/// SQLite-backed cookie jar. Wraps a <see cref="CookieContainer"/> so HttpClient sees the
/// in-memory jar at native speed; <see cref="LoadAsync"/> rehydrates from disk on startup
/// and <see cref="PersistAsync"/> writes changes back. The HttpExecutor calls
/// PersistAsync after each request so users don't lose session cookies on app exit.
///
/// Default path: <c>%LocalAppData%/Vegha/cookies.db</c>. The schema keeps one row per
/// (domain, path, name) cookie identity — Set-Cookie of the same triple replaces in place.
///
/// The desktop app, CLI and VS Code sidecar share the file, so a persist writes only what
/// changed in this process since its last sync (upserts + deletes) rather than replacing the
/// table, then pulls in what other processes wrote. A <c>meta.version</c> counter bumped on
/// every write lets <see cref="RefreshIfChangedAsync"/> skip the reload when nothing moved.
/// </summary>
public sealed class CookieJarStore : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>What the database held at this instance's last load/persist, keyed by cookie
    /// identity. Diffing the live container against it yields this process's own changes.</summary>
    private Dictionary<CookieKey, StoredCookie> _synced = new();
    private long _syncedVersion = -1;

    public string DatabasePath { get; }
    public CookieContainer Container { get; } = new();

    /// <summary>Completes once the initial disk load has rehydrated <see cref="Container"/>.
    /// Callers that need a populated jar (the HTTP executor) await this before reading.
    /// Defaults to a completed task so tests/code paths that don't call
    /// <see cref="BeginInitialLoad"/> never block.</summary>
    public Task ReadyAsync { get; private set; } = Task.CompletedTask;

    public CookieJarStore() : this(DefaultDatabasePath()) { }

    public CookieJarStore(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = SqliteMultiProcess.ConnectionString(databasePath);

        EnsureSchema();
    }

    public static string DefaultDatabasePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha",
            "cookies.db");

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        SqliteMultiProcess.EnableWal(conn);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS cookies (
                domain      TEXT NOT NULL,
                path        TEXT NOT NULL,
                name        TEXT NOT NULL,
                value       TEXT NOT NULL,
                expires_utc INTEGER,
                http_only   INTEGER NOT NULL DEFAULT 0,
                secure      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (domain, path, name)
            );
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            """);
    }

    /// <summary>Kicks off the initial load on a worker thread and exposes it via
    /// <see cref="ReadyAsync"/>. Callers can fire-and-forget at startup; HTTP requests
    /// await ReadyAsync before reading the jar so cold-start doesn't block on SQLite.</summary>
    public void BeginInitialLoad()
    {
        ReadyAsync = Task.Run(async () =>
        {
            try { await LoadAsync().ConfigureAwait(false); }
            catch { /* fresh jar on failure — matches previous behavior */ }
        });
    }

    /// <summary>Reads every persisted cookie into the in-memory container. Expired cookies are skipped.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            var (rows, version) = await ReadAllAsync(conn, null).ConfigureAwait(false);
            ApplyToContainer(rows);
            _synced = rows;
            _syncedVersion = version;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Writes this process's changes since its last sync (new/changed cookies are
    /// upserted, cookies it removed or that expired are deleted), then merges in whatever
    /// other processes wrote meanwhile. Cookies another process added are never dropped.</summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await PersistCoreAsync().ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Cheap check (one row read) for writes made by another process since this
    /// instance last synced; when there are any, merges them into <see cref="Container"/>.
    /// The HTTP executor calls this before each send so a login in the desktop app is
    /// visible to the next request from VS Code, and vice versa.</summary>
    public async Task RefreshIfChangedAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long version;
            using (var conn = OpenConnection())
                version = await ReadVersionAsync(conn, null).ConfigureAwait(false);
            if (version == _syncedVersion) return;
            // Persist (not a bare reload) so unsaved local changes survive the merge.
            await PersistCoreAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task PersistCoreAsync()
    {
        var live = LiveCookies();
        var upserts = live.Values
            .Where(c => !_synced.TryGetValue(c.Key, out var prev) || prev != c)
            .ToList();
        var deletes = _synced.Values.Where(c => !live.ContainsKey(c.Key)).ToList();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = OpenConnection();
        // Non-deferred → BEGIN IMMEDIATE: take the write lock up front so the read-back
        // below sees exactly the state our writes produced.
        using var tx = conn.BeginTransaction(deferred: false);
        foreach (var c in deletes)
        {
            await conn.ExecuteAsync(
                "DELETE FROM cookies WHERE domain = @Domain AND path = @Path AND name = @Name;",
                new { c.Domain, c.Path, c.Name }, tx).ConfigureAwait(false);
        }
        foreach (var c in upserts)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO cookies (domain, path, name, value, expires_utc, http_only, secure)
                VALUES (@Domain, @Path, @Name, @Value, @Exp, @HttpOnly, @Secure)
                ON CONFLICT (domain, path, name) DO UPDATE SET
                    value = excluded.value, expires_utc = excluded.expires_utc,
                    http_only = excluded.http_only, secure = excluded.secure;
                """,
                new
                {
                    c.Domain,
                    c.Path,
                    c.Name,
                    c.Value,
                    Exp = c.ExpiresUtc,
                    HttpOnly = c.HttpOnly ? 1 : 0,
                    Secure = c.Secure ? 1 : 0,
                },
                tx).ConfigureAwait(false);
        }
        var pruned = await conn.ExecuteAsync(
            "DELETE FROM cookies WHERE expires_utc IS NOT NULL AND expires_utc <= @Now;",
            new { Now = nowMs }, tx).ConfigureAwait(false);
        if (upserts.Count > 0 || deletes.Count > 0 || pruned > 0)
        {
            await conn.ExecuteAsync(
                "INSERT INTO meta (key, value) VALUES ('version', 1) ON CONFLICT (key) DO UPDATE SET value = value + 1;",
                transaction: tx).ConfigureAwait(false);
        }
        var (rows, version) = await ReadAllAsync(conn, tx).ConfigureAwait(false);
        tx.Commit();

        ApplyToContainer(rows);
        _synced = rows;
        _syncedVersion = version;
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection conn, SqliteTransaction? tx) =>
        await conn.ExecuteScalarAsync<long?>(
            "SELECT value FROM meta WHERE key = 'version';", transaction: tx).ConfigureAwait(false) ?? 0;

    private static async Task<(Dictionary<CookieKey, StoredCookie> Rows, long Version)> ReadAllAsync(
        SqliteConnection conn, SqliteTransaction? tx)
    {
        var rows = await conn.QueryAsync<CookieRow>(
            "SELECT domain, path, name, value, expires_utc AS ExpiresUtc, http_only AS HttpOnly, secure FROM cookies",
            transaction: tx).ConfigureAwait(false);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var map = new Dictionary<CookieKey, StoredCookie>();
        foreach (var r in rows)
        {
            if (r.ExpiresUtc is { } exp && exp <= nowMs) continue;
            var c = new StoredCookie(r.Domain, NormalizePath(r.Path), r.Name, r.Value, r.ExpiresUtc, r.HttpOnly != 0, r.Secure != 0);
            map[c.Key] = c;
        }
        return (map, await ReadVersionAsync(conn, tx).ConfigureAwait(false));
    }

    /// <summary>Makes <see cref="Container"/> match <paramref name="rows"/>: cookies absent from
    /// the database are soft-deleted (past Expires), differing or missing ones are (re)added.
    /// Cookies already identical are left alone so HttpClient's own entries aren't churned.</summary>
    private void ApplyToContainer(Dictionary<CookieKey, StoredCookie> rows)
    {
        var past = DateTime.UtcNow.AddYears(-1);
        var live = LiveCookies();
        foreach (Cookie c in Container.GetAllCookies())
        {
            var key = StoredCookie.From(c).Key;
            if (live.ContainsKey(key) && !rows.ContainsKey(key)) c.Expires = past;
        }
        foreach (var row in rows.Values)
        {
            if (live.TryGetValue(row.Key, out var current) && current == row) continue;
            var cookie = new Cookie(row.Name, row.Value, row.Path, row.Domain)
            {
                HttpOnly = row.HttpOnly,
                Secure = row.Secure,
            };
            if (row.ExpiresUtc.HasValue)
                cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresUtc.Value).UtcDateTime;

            try { Container.Add(cookie); }
            catch { /* malformed cookie — skip rather than fail load */ }
        }
    }

    /// <summary>Non-expired cookies currently in the container, keyed by identity.</summary>
    private Dictionary<CookieKey, StoredCookie> LiveCookies()
    {
        var now = DateTime.UtcNow;
        var map = new Dictionary<CookieKey, StoredCookie>();
        foreach (Cookie c in Container.GetAllCookies())
        {
            if (c.Expired) continue;
            // Past-dated Expires marks our soft-deleted cookies; they are not live.
            if (c.Expires != DateTime.MinValue && c.Expires.ToUniversalTime() <= now) continue;
            var stored = StoredCookie.From(c);
            map[stored.Key] = stored;
        }
        return map;
    }

    private static string NormalizePath(string? path) => string.IsNullOrEmpty(path) ? "/" : path;

    /// <summary>Returns a flat snapshot of every cookie in the in-memory container.
    /// Cookies whose Expires is in the past are filtered out — CookieContainer doesn't
    /// physically remove them on its own, so we treat past-Expires as the deletion marker.</summary>
    public IReadOnlyList<CookieRecord> GetAll()
    {
        var snapshot = new List<CookieRecord>();
        var now = DateTime.UtcNow;
        foreach (Cookie c in Container.GetAllCookies())
        {
            // Skip soft-deleted cookies (Expires set to past via RemoveAsync/ClearAsync).
            if (c.Expires != DateTime.MinValue && c.Expires <= now) continue;
            snapshot.Add(new CookieRecord(
                Domain: c.Domain,
                Path: string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                Name: c.Name,
                Value: c.Value,
                Expires: c.Expires == DateTime.MinValue ? null : c.Expires,
                HttpOnly: c.HttpOnly,
                Secure: c.Secure));
        }
        return snapshot;
    }

    /// <summary>Removes a single cookie by domain + path + name. Use <see cref="ClearAsync"/> for "delete all".
    /// Sets the cookie's Expires to the past — CookieContainer suppresses them from outgoing requests
    /// based on Expires, and our <see cref="GetAll"/> + <see cref="PersistAsync"/> filters skip them.</summary>
    public async Task RemoveAsync(string domain, string path, string name, CancellationToken ct = default)
    {
        var match = Container.GetAllCookies()
            .Cast<Cookie>()
            .FirstOrDefault(c =>
                string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Path, path, StringComparison.Ordinal) &&
                string.Equals(c.Name, name, StringComparison.Ordinal));
        if (match is not null) match.Expires = DateTime.UtcNow.AddYears(-1);

        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes every cookie from the in-memory container and on-disk store.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var pastExpires = DateTime.UtcNow.AddYears(-1);
        foreach (Cookie c in Container.GetAllCookies()) c.Expires = pastExpires;
        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Adds or replaces a cookie. CookieContainer.Add deduplicates by domain+path+
    /// name automatically — the new entry overwrites any existing match. Persists to disk.</summary>
    public async Task UpsertAsync(
        string domain, string path, string name, string value,
        DateTime? expires = null, bool httpOnly = false, bool secure = false,
        CancellationToken ct = default)
    {
        var cookie = new Cookie(name, value, string.IsNullOrEmpty(path) ? "/" : path, domain)
        {
            HttpOnly = httpOnly,
            Secure = secure,
        };
        if (expires.HasValue) cookie.Expires = expires.Value;
        Container.Add(cookie);
        await PersistAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _writeLock.Dispose();

    private SqliteConnection OpenConnection() => SqliteMultiProcess.Open(_connectionString);

    private readonly record struct CookieKey(string Domain, string Path, string Name);

    /// <summary>One cookie as the database stores it. Record equality is the change test.</summary>
    private sealed record StoredCookie(
        string Domain, string Path, string Name, string Value, long? ExpiresUtc, bool HttpOnly, bool Secure)
    {
        // Domains compare case-insensitively (RFC 6265 §5.1.3); paths and names are exact.
        public CookieKey Key => new(Domain.ToLowerInvariant(), Path, Name);

        public static StoredCookie From(Cookie c) => new(
            c.Domain,
            NormalizePath(c.Path),
            c.Name,
            c.Value,
            c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeMilliseconds(),
            c.HttpOnly,
            c.Secure);
    }

    private sealed class CookieRow
    {
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = "/";
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public long? ExpiresUtc { get; set; }
        public long HttpOnly { get; set; }
        public long Secure { get; set; }
    }
}

/// <summary>Flat snapshot row — what the cookies viewer panel binds to.</summary>
public sealed record CookieRecord(
    string Domain,
    string Path,
    string Name,
    string Value,
    DateTime? Expires,
    bool HttpOnly,
    bool Secure);
