using System.Globalization;
using Microsoft.Data.Sqlite;
using Omega.Core.Models;

namespace Omega.Core.Persistence;

/// <summary>
/// <see cref="ILibraryStore"/> over Microsoft.Data.Sqlite (design §6.1):
/// hand-written parameterized SQL and hand-written row mapping only —
/// no EF/Dapper/reflection materializers, so nothing here can break
/// under trimming or Native AOT.
/// </summary>
/// <remarks>
/// <para>
/// The constructor takes the full database <b>file path</b> so tests can
/// point the store at temp files; the app either passes its packaged
/// <c>ApplicationData.LocalFolder</c> path explicitly or uses
/// <see cref="CreateDefault"/> (LocalApplicationData).</para>
/// <para>
/// One instance is expected to be shared app-wide. Every operation is
/// serialized through an internal gate and opens its own short-lived
/// connection (WAL mode) — the simple, correct shape for a
/// single-process library database. All public methods initialize the
/// schema lazily on first use; <see cref="InitializeAsync"/> exists so
/// the app can warm that up at startup.</para>
/// <para>
/// Timestamps the store assigns itself (added_at, played_at, updated_at)
/// are unix epoch milliseconds from a strictly-increasing counter seeded
/// by the wall clock, so same-millisecond writes still order
/// deterministically (most importantly: history newest-first).</para>
/// </remarks>
public sealed class SqliteLibraryStore : ILibraryStore
{
    /// <summary>The database file name used by <see cref="CreateDefault"/> (design §6.1).</summary>
    public const string DatabaseFileName = "omega.db";

    /// <summary>History is pruned to this many entries on every recorded play (design §6.2).</summary>
    private const int HistoryCap = 500;

    private static readonly object BatteriesGate = new();
    private static bool _batteriesInitialized;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private long _lastTimestampMs;

    public SqliteLibraryStore(string databaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFilePath);
        _databasePath = databaseFilePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>The full path of the database file this store owns.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// A store at <c>%LocalAppData%\Omega\omega.db</c> — the unmanaged
    /// equivalent of the packaged app's <c>LocalFolder</c> that design
    /// §6.1 targets. The packaged app may instead construct the store
    /// with <c>Path.Combine(ApplicationData.Current.LocalFolder.Path, "omega.db")</c>.
    /// </summary>
    public static SqliteLibraryStore CreateDefault()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Omega");
        return new SqliteLibraryStore(Path.Combine(directory, DatabaseFileName));
    }

    // ------------------------------------------------------------------
    // ILibraryStore
    // ------------------------------------------------------------------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<Song>> GetFavoritesAsync(CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "SELECT snapshot FROM favorites ORDER BY added_at DESC;");
            var songs = new List<Song>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                songs.Add(SongSnapshot.FromJson(reader.GetString(0)).ToSong());
            }

            return (IReadOnlyList<Song>)songs;
        }, ct);

    public Task<bool> IsFavoriteAsync(string songId, CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "SELECT COUNT(*) FROM favorites WHERE song_id = $songId;",
                ("$songId", songId));
            long count = (long)(await cmd.ExecuteScalarAsync(token))!;
            return count > 0;
        }, ct);

    public Task SetFavoriteAsync(Song song, bool isFavorite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        return RunAsync(async (connection, token) =>
        {
            if (isFavorite)
            {
                // Re-favoriting refreshes the snapshot but keeps the
                // original added_at (and thus the favorite's position).
                using SqliteCommand cmd = CreateCommand(
                    connection,
                    """
                    INSERT INTO favorites (song_id, snapshot, added_at)
                    VALUES ($songId, $snapshot, $addedAt)
                    ON CONFLICT(song_id) DO UPDATE SET snapshot = excluded.snapshot;
                    """,
                    ("$songId", song.Id),
                    ("$snapshot", SongSnapshot.FromSong(song).ToJson()),
                    ("$addedAt", NextTimestampMs()));
                await cmd.ExecuteNonQueryAsync(token);
            }
            else
            {
                using SqliteCommand cmd = CreateCommand(
                    connection,
                    "DELETE FROM favorites WHERE song_id = $songId;",
                    ("$songId", song.Id));
                await cmd.ExecuteNonQueryAsync(token);
            }
        }, ct);
    }

    public Task<IReadOnlyList<LibraryPlaylist>> GetPlaylistsAsync(CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                """
                SELECT p.id, p.name, p.updated_at, COUNT(s.song_id)
                FROM playlists p
                LEFT JOIN playlist_songs s ON s.playlist_id = p.id
                GROUP BY p.id, p.name, p.updated_at, p.created_at
                ORDER BY p.created_at ASC, p.id ASC;
                """);
            var playlists = new List<LibraryPlaylist>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                playlists.Add(new LibraryPlaylist(
                    Id: reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                    Name: reader.GetString(1),
                    SongCount: (int)reader.GetInt64(3),
                    UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }

            return (IReadOnlyList<LibraryPlaylist>)playlists;
        }, ct);

    public Task<LibraryPlaylist> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(async (connection, token) =>
        {
            long now = NextTimestampMs();
            using (SqliteCommand insert = CreateCommand(
                connection,
                "INSERT INTO playlists (name, created_at, updated_at) VALUES ($name, $now, $now);",
                ("$name", name),
                ("$now", now)))
            {
                await insert.ExecuteNonQueryAsync(token);
            }

            using SqliteCommand idQuery = CreateCommand(connection, "SELECT last_insert_rowid();");
            long id = (long)(await idQuery.ExecuteScalarAsync(token))!;
            return new LibraryPlaylist(
                Id: id.ToString(CultureInfo.InvariantCulture),
                Name: name,
                SongCount: 0,
                UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(now));
        }, ct);
    }

    public Task RenamePlaylistAsync(string playlistId, string name, CancellationToken ct = default)
    {
        if (!TryParsePlaylistId(playlistId, out long id))
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "UPDATE playlists SET name = $name, updated_at = $now WHERE id = $id;",
                ("$name", name),
                ("$now", NextTimestampMs()),
                ("$id", id));
            await cmd.ExecuteNonQueryAsync(token);
        }, ct);
    }

    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        if (!TryParsePlaylistId(playlistId, out long id))
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (connection, token) =>
        {
            // playlist_songs rows go with it via ON DELETE CASCADE
            // (foreign_keys pragma is set on every connection).
            using SqliteCommand cmd = CreateCommand(
                connection,
                "DELETE FROM playlists WHERE id = $id;",
                ("$id", id));
            await cmd.ExecuteNonQueryAsync(token);
        }, ct);
    }

    public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default)
    {
        if (!TryParsePlaylistId(playlistId, out long id))
        {
            return Task.FromResult<IReadOnlyList<Song>>(Array.Empty<Song>());
        }

        return RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "SELECT snapshot FROM playlist_songs WHERE playlist_id = $id ORDER BY position ASC;",
                ("$id", id));
            var songs = new List<Song>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                songs.Add(SongSnapshot.FromJson(reader.GetString(0)).ToSong());
            }

            return (IReadOnlyList<Song>)songs;
        }, ct);
    }

    public Task AddSongToPlaylistAsync(string playlistId, Song song, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!TryParsePlaylistId(playlistId, out long id))
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (connection, token) =>
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            long nextPosition;
            using (SqliteCommand posQuery = CreateCommand(
                connection,
                "SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_songs WHERE playlist_id = $id;",
                ("$id", id)))
            {
                posQuery.Transaction = tx;
                nextPosition = (long)(await posQuery.ExecuteScalarAsync(token))!;
            }

            using (SqliteCommand insert = CreateCommand(
                connection,
                """
                INSERT INTO playlist_songs (playlist_id, song_id, snapshot, position)
                VALUES ($playlistId, $songId, $snapshot, $position)
                ON CONFLICT(playlist_id, song_id) DO UPDATE SET
                    snapshot = excluded.snapshot,
                    position = excluded.position;
                """,
                ("$playlistId", id),
                ("$songId", song.Id),
                ("$snapshot", SongSnapshot.FromSong(song).ToJson()),
                ("$position", nextPosition)))
            {
                insert.Transaction = tx;
                await insert.ExecuteNonQueryAsync(token);
            }

            using (SqliteCommand touch = CreateCommand(
                connection,
                "UPDATE playlists SET updated_at = $now WHERE id = $id;",
                ("$now", NextTimestampMs()),
                ("$id", id)))
            {
                touch.Transaction = tx;
                await touch.ExecuteNonQueryAsync(token);
            }

            await tx.CommitAsync(token);
        }, ct);
    }

    public Task RemoveSongFromPlaylistAsync(string playlistId, string songId, CancellationToken ct = default)
    {
        if (!TryParsePlaylistId(playlistId, out long id))
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "DELETE FROM playlist_songs WHERE playlist_id = $playlistId AND song_id = $songId;",
                ("$playlistId", id),
                ("$songId", songId));
            await cmd.ExecuteNonQueryAsync(token);

            using SqliteCommand touch = CreateCommand(
                connection,
                "UPDATE playlists SET updated_at = $now WHERE id = $id;",
                ("$now", NextTimestampMs()),
                ("$id", id));
            await touch.ExecuteNonQueryAsync(token);
        }, ct);
    }

    public Task RecordPlayAsync(Song song, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        return RunAsync(async (connection, token) =>
        {
            // De-dupe by song: a re-play refreshes the snapshot and the
            // played_at, which moves the song to the top of history.
            using (SqliteCommand upsert = CreateCommand(
                connection,
                """
                INSERT INTO history (song_id, snapshot, played_at)
                VALUES ($songId, $snapshot, $playedAt)
                ON CONFLICT(song_id) DO UPDATE SET
                    snapshot = excluded.snapshot,
                    played_at = excluded.played_at;
                """,
                ("$songId", song.Id),
                ("$snapshot", SongSnapshot.FromSong(song).ToJson()),
                ("$playedAt", NextTimestampMs())))
            {
                await upsert.ExecuteNonQueryAsync(token);
            }

            using SqliteCommand prune = CreateCommand(
                connection,
                """
                DELETE FROM history
                WHERE song_id IN (
                    SELECT song_id FROM history ORDER BY played_at DESC LIMIT -1 OFFSET $cap
                );
                """,
                ("$cap", HistoryCap));
            await prune.ExecuteNonQueryAsync(token);
        }, ct);
    }

    public Task<IReadOnlyList<Song>> GetHistoryAsync(int limit = 100, CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "SELECT snapshot FROM history ORDER BY played_at DESC LIMIT $limit;",
                ("$limit", limit));
            var songs = new List<Song>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                songs.Add(SongSnapshot.FromJson(reader.GetString(0)).ToSong());
            }

            return (IReadOnlyList<Song>)songs;
        }, ct);

    public Task ClearHistoryAsync(CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(connection, "DELETE FROM history;");
            await cmd.ExecuteNonQueryAsync(token);
        }, ct);

    public Task<IReadOnlyList<DownloadRecord>> GetDownloadsAsync(CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                """
                SELECT song_id, title, artists, image_url, local_path, status, progress, error_message, updated_at
                FROM downloads
                ORDER BY updated_at DESC;
                """);
            var records = new List<DownloadRecord>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                records.Add(new DownloadRecord(
                    SongId: reader.GetString(0),
                    Title: reader.GetString(1),
                    Artists: reader.GetString(2),
                    ImageUrl: GetNullableString(reader, 3),
                    LocalPath: GetNullableString(reader, 4),
                    Status: (DownloadStatus)reader.GetInt32(5),
                    Progress: reader.GetInt32(6),
                    ErrorMessage: GetNullableString(reader, 7),
                    UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
            }

            return (IReadOnlyList<DownloadRecord>)records;
        }, ct);

    public Task UpsertDownloadAsync(DownloadRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                """
                INSERT INTO downloads (song_id, title, artists, image_url, local_path, status, progress, error_message, updated_at)
                VALUES ($songId, $title, $artists, $imageUrl, $localPath, $status, $progress, $errorMessage, $updatedAt)
                ON CONFLICT(song_id) DO UPDATE SET
                    title = excluded.title,
                    artists = excluded.artists,
                    image_url = excluded.image_url,
                    local_path = excluded.local_path,
                    status = excluded.status,
                    progress = excluded.progress,
                    error_message = excluded.error_message,
                    updated_at = excluded.updated_at;
                """,
                ("$songId", record.SongId),
                ("$title", record.Title),
                ("$artists", record.Artists),
                ("$imageUrl", record.ImageUrl),
                ("$localPath", record.LocalPath),
                ("$status", (int)record.Status),
                ("$progress", record.Progress),
                ("$errorMessage", record.ErrorMessage),
                ("$updatedAt", record.UpdatedAt.ToUnixTimeMilliseconds()));
            await cmd.ExecuteNonQueryAsync(token);
        }, ct);
    }

    public Task DeleteDownloadAsync(string songId, CancellationToken ct = default) =>
        RunAsync(async (connection, token) =>
        {
            using SqliteCommand cmd = CreateCommand(
                connection,
                "DELETE FROM downloads WHERE song_id = $songId;",
                ("$songId", songId));
            await cmd.ExecuteNonQueryAsync(token);
        }, ct);

    // ------------------------------------------------------------------
    // Infrastructure
    // ------------------------------------------------------------------

    private async Task RunAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            await using SqliteConnection connection = await OpenConnectionAsync(ct);
            await operation(connection, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            await using SqliteConnection connection = await OpenConnectionAsync(ct);
            return await operation(connection, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates the directory/file if needed, enables WAL, and creates the
    /// schema when absent. Caller must hold the gate. Safe to call
    /// repeatedly — after the first success it is a no-op.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = await OpenConnectionAsync(ct);

        using (SqliteCommand wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteScalarAsync(ct);
        }

        using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = SchemaSql;
            await schema.ExecuteNonQueryAsync(ct);
        }

        // Schema version marker for future hand-written migrations
        // (PRAGMA user_version, the Room analogue — design §6.1).
        // The value is our own constant, never user input.
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = 1;";
            await version.ExecuteNonQueryAsync(ct);
        }

        _initialized = true;
    }

    /// <summary>
    /// Opens a new connection with foreign-key enforcement on (a
    /// per-connection pragma in SQLite). Caller owns disposal.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        EnsureBatteries();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Initializes the SQLitePCLRaw bundle exactly once per process.
    /// Must run before any <see cref="SqliteConnection"/> opens.
    /// </summary>
    private static void EnsureBatteries()
    {
        lock (BatteriesGate)
        {
            if (_batteriesInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _batteriesInitialized = true;
        }
    }

    /// <summary>
    /// Strictly-increasing epoch-millisecond timestamps (see class
    /// remarks). Callers hold the store gate; no extra locking needed.
    /// </summary>
    private long NextTimestampMs()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastTimestampMs = Math.Max(now, _lastTimestampMs + 1);
        return _lastTimestampMs;
    }

    private static bool TryParsePlaylistId(string playlistId, out long id) =>
        long.TryParse(playlistId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
        }

        return command;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// The library schema (design §6.2, mirroring the Android Room
    /// entities). Song snapshots are single JSON TEXT columns (see
    /// <see cref="SongSnapshot"/>); every other column is a queryable
    /// scalar. Timestamps are unix epoch milliseconds. Statements are
    /// idempotent (<c>IF NOT EXISTS</c>).
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS favorites (
            song_id  TEXT PRIMARY KEY,
            snapshot TEXT NOT NULL,
            added_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS playlists (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS playlist_songs (
            playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            song_id     TEXT NOT NULL,
            snapshot    TEXT NOT NULL,
            position    INTEGER NOT NULL,
            PRIMARY KEY (playlist_id, song_id)
        );
        CREATE TABLE IF NOT EXISTS history (
            song_id   TEXT PRIMARY KEY,
            snapshot  TEXT NOT NULL,
            played_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS downloads (
            song_id       TEXT PRIMARY KEY,
            title         TEXT NOT NULL,
            artists       TEXT NOT NULL,
            image_url     TEXT,
            local_path    TEXT,
            status        INTEGER NOT NULL,
            progress      INTEGER NOT NULL,
            error_message TEXT,
            updated_at    INTEGER NOT NULL
        );
        """;
}
