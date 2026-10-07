using Omega.Core.Models;

namespace Omega.Core.Persistence;

/// <summary>
/// The local library store (design §6): favorites, user playlists, play
/// history and download records, persisted on-device in SQLite. This
/// interface is the contract the app layer (Library / Detail / Player
/// pages) codes against; the implementation is
/// <see cref="SqliteLibraryStore"/> — hand-written SQL over
/// Microsoft.Data.Sqlite, no ORM and no reflection-based mapping
/// (Native AOT requirement, design §6.1).
/// </summary>
/// <remarks>
/// All songs are stored as full snapshots (see <see cref="SongSnapshot"/>),
/// so every read renders offline without an upstream fetch. Mutations
/// against a playlist id that does not exist (or does not parse) are
/// silent no-ops; reads for one return empty lists — the UI never has to
/// defend against a stale id.
/// </remarks>
public interface ILibraryStore
{
    /// <summary>
    /// Creates the database directory/file if needed and applies the
    /// schema. Idempotent; the implementation also initializes lazily on
    /// first use, so calling this is an explicit warm-up, not a
    /// precondition.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Favorite songs, most recently favorited first.</summary>
    Task<IReadOnlyList<Song>> GetFavoritesAsync(CancellationToken ct = default);

    /// <summary>True when the song is currently a favorite.</summary>
    Task<bool> IsFavoriteAsync(string songId, CancellationToken ct = default);

    /// <summary>
    /// Adds (<paramref name="isFavorite"/> = true) or removes the song
    /// from favorites. Adding an existing favorite refreshes its snapshot
    /// but keeps its original position.
    /// </summary>
    Task SetFavoriteAsync(Song song, bool isFavorite, CancellationToken ct = default);

    /// <summary>All user playlists in creation order, with song counts.</summary>
    Task<IReadOnlyList<LibraryPlaylist>> GetPlaylistsAsync(CancellationToken ct = default);

    /// <summary>Creates an empty playlist and returns it (with its new id).</summary>
    Task<LibraryPlaylist> CreatePlaylistAsync(string name, CancellationToken ct = default);

    /// <summary>Renames a playlist; no-op when the id is unknown.</summary>
    Task RenamePlaylistAsync(string playlistId, string name, CancellationToken ct = default);

    /// <summary>Deletes a playlist and all its memberships; no-op when unknown.</summary>
    Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>The playlist's songs in stored (append) order.</summary>
    Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Appends the song at the end of the playlist (max position + 1).
    /// Re-adding a song already in the playlist refreshes its snapshot
    /// and moves it to the end.
    /// </summary>
    Task AddSongToPlaylistAsync(string playlistId, Song song, CancellationToken ct = default);

    /// <summary>Removes one song from a playlist; remaining order is preserved.</summary>
    Task RemoveSongFromPlaylistAsync(string playlistId, string songId, CancellationToken ct = default);

    /// <summary>
    /// Records a play: the song becomes the newest history entry.
    /// Re-playing a song already in history moves it to the top
    /// (history is de-duped by song). History is capped at 500 entries.
    /// </summary>
    Task RecordPlayAsync(Song song, CancellationToken ct = default);

    /// <summary>Play history, newest first, at most <paramref name="limit"/> entries.</summary>
    Task<IReadOnlyList<Song>> GetHistoryAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Empties the play history.</summary>
    Task ClearHistoryAsync(CancellationToken ct = default);

    /// <summary>Download records, most recently updated first.</summary>
    Task<IReadOnlyList<DownloadRecord>> GetDownloadsAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces the download record for its song id.</summary>
    Task UpsertDownloadAsync(DownloadRecord record, CancellationToken ct = default);

    /// <summary>Deletes the download record for a song; no-op when absent.</summary>
    Task DeleteDownloadAsync(string songId, CancellationToken ct = default);
}
