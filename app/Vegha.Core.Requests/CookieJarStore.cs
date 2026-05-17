using System.Data;
using System.Net;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Vegha.Core.Requests;

/// <summary>
/// SQLite-backed cookie jar. Wraps a <see cref="CookieContainer"/> so HttpClient sees the
/// in-memory jar at native speed; <see cref="LoadAsync"/> rehydrates from disk on startup
/// and <see cref="PersistAsync"/> writes the full snapshot back. The HttpExecutor calls
/// PersistAsync after each request so users don't lose session cookies on app exit.
///
/// Default path: <c>%LocalAppData%/Vegha/cookies.db</c>. The schema keeps one row per
/// (domain, path, name) cookie identity — Set-Cookie of the same triple replaces in place.
/// </summary>
public sealed class CookieJarStore : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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
            "cookies.db");

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
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
            var rows = await conn.QueryAsync<CookieRow>(
                "SELECT domain, path, name, value, expires_utc AS ExpiresUtc, http_only AS HttpOnly, secure FROM cookies"
            ).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                if (r.ExpiresUtc.HasValue)
                {
                    var expires = DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresUtc.Value).UtcDateTime;
                    if (expires <= now) continue;
                }

                var cookie = new Cookie(r.Name, r.Value, r.Path, r.Domain)
                {
                    HttpOnly = r.HttpOnly != 0,
                    Secure = r.Secure != 0,
                };
                if (r.ExpiresUtc.HasValue)
                    cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresUtc.Value).UtcDateTime;

                try { Container.Add(cookie); }
                catch { /* malformed cookie — skip rather than fail load */ }
            }
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Snapshots the in-memory container to SQLite as a full replacement.</summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = Container.GetAllCookies(); // .NET 6+
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM cookies;", transaction: tx).ConfigureAwait(false);
            var nowUtc = DateTime.UtcNow;
            foreach (Cookie c in all)
            {
                if (c.Expired) continue;
                // Past-dated Expires marks our soft-deleted cookies; do not persist them.
                if (c.Expires != DateTime.MinValue && c.Expires <= nowUtc) continue;
                long? exp = c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires, TimeSpan.Zero).ToUnixTimeMilliseconds();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO cookies (domain, path, name, value, expires_utc, http_only, secure)
                    VALUES (@Domain, @Path, @Name, @Value, @Exp, @HttpOnly, @Secure);
                    """,
                    new
                    {
                        Domain = c.Domain,
                        Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                        Name = c.Name,
                        Value = c.Value,
                        Exp = exp,
                        HttpOnly = c.HttpOnly ? 1 : 0,
                        Secure = c.Secure ? 1 : 0,
                    },
                    transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

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

    private IDbConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
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
