using Omega.Core.Models;
using Omega.Core.Persistence;
using Xunit;

namespace Omega.Core.Tests;

/// <summary>
/// The SQLite library store (design §6) against real temp database
/// files — no mocks anywhere near the SQL. Each test gets its own file,
/// so the suite is order- and parallelism-independent.
/// </summary>
public class LibraryStoreTests
{
    private static string NewTempDbPath()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "omega-library-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "library.db");
    }

    private static void TryDeleteDb(string dbPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only: a lingering -wal/-shm handle on Windows
            // must not fail the suite over a temp directory.
        }
    }

    private static Song MakeSong(string id, string name = "Song") => new(
        Id: id,
        Name: name,
        Subtitle: "Artist One",
        Url: "https://www.jiosaavn.com/song/x/" + id,
        Image: new ImageSet(
            "https://c.saavncdn.com/430/X-50x50.jpg",
            "https://c.saavncdn.com/430/X-150x150.jpg",
            "https://c.saavncdn.com/430/X-500x500.jpg"),
        Language: "hindi",
        Year: 2024,
        PlayCount: 12345,
        Explicit: false,
        DurationSeconds: 200,
        ReleaseDate: "2024-01-01",
        Label: "T-Series",
        HasLyrics: true,
        Copyright: null,
        AlbumId: "alb1",
        AlbumName: "The Album",
        AlbumUrl: null,
        Artists: new ArtistGroups(
            Primary: [new ArtistRef("a1", "Artist One", "primary_artists", ImageSet.Empty, null)],
            Featured: [],
            All: [new ArtistRef("a1", "Artist One", "primary_artists", ImageSet.Empty, null)]),
        StreamUrls:
        [
            new QualityUrl("96kbps", 96, "https://aac.saavncdn.com/450/" + id + "_96.mp4"),
            new QualityUrl("320kbps", 320, "https://aac.saavncdn.com/450/" + id + "_320.mp4"),
        ],
        Is320Kbps: true);

    [Fact]
    public async Task Initialize_CalledTwice_IsSafe_AndLazyInitWorks()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);
            await store.InitializeAsync();
            await store.InitializeAsync();
            Assert.Empty(await store.GetFavoritesAsync());

            // No explicit InitializeAsync at all — first use initializes.
            var lazy = new SqliteLibraryStore(NewTempDbPath());
            Assert.Empty(await lazy.GetHistoryAsync());
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task Favorites_Toggle_AddsThenRemoves()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);
            Song song = MakeSong("fav1", "Tum Hi Ho");

            Assert.False(await store.IsFavoriteAsync(song.Id));
            await store.SetFavoriteAsync(song, isFavorite: true);

            Assert.True(await store.IsFavoriteAsync(song.Id));
            IReadOnlyList<Song> favorites = await store.GetFavoritesAsync();
            Song stored = Assert.Single(favorites);
            Assert.Equal("fav1", stored.Id);
            Assert.Equal("Tum Hi Ho", stored.Name);

            await store.SetFavoriteAsync(song, isFavorite: false);
            Assert.False(await store.IsFavoriteAsync(song.Id));
            Assert.Empty(await store.GetFavoritesAsync());
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task Favorites_SnapshotRoundTrips_AllSongFields_AndSurvivesNewInstance()
    {
        string dbPath = NewTempDbPath();
        try
        {
            Song song = MakeSong("fav2");
            var first = new SqliteLibraryStore(dbPath);
            await first.SetFavoriteAsync(song, isFavorite: true);

            var second = new SqliteLibraryStore(dbPath);
            Song stored = Assert.Single(await second.GetFavoritesAsync());

            Assert.Equal(song.Id, stored.Id);
            Assert.Equal(song.Name, stored.Name);
            Assert.Equal(song.Subtitle, stored.Subtitle);
            Assert.Equal(song.Url, stored.Url);
            Assert.Equal(song.Image, stored.Image);
            Assert.Equal(song.Language, stored.Language);
            Assert.Equal(song.Year, stored.Year);
            Assert.Equal(song.PlayCount, stored.PlayCount);
            Assert.Equal(song.Explicit, stored.Explicit);
            Assert.Equal(song.DurationSeconds, stored.DurationSeconds);
            Assert.Equal(song.ReleaseDate, stored.ReleaseDate);
            Assert.Equal(song.Label, stored.Label);
            Assert.Equal(song.HasLyrics, stored.HasLyrics);
            Assert.Equal(song.AlbumId, stored.AlbumId);
            Assert.Equal(song.AlbumName, stored.AlbumName);
            Assert.Equal("Artist One", stored.PrimaryArtistNames);
            Assert.Equal("a1", stored.Artists.Primary[0].Id);
            Assert.Equal(2, stored.StreamUrls.Count);
            Assert.Equal(song.StreamUrls[1].Url, stored.StreamUrlFor("320kbps"));
            Assert.True(stored.Is320Kbps);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task Playlists_CreateAddAppendOrderRemoveRenameDelete()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);

            LibraryPlaylist roadTrip = await store.CreatePlaylistAsync("Road Trip");
            LibraryPlaylist chill = await store.CreatePlaylistAsync("Chill");
            Assert.False(string.IsNullOrEmpty(roadTrip.Id));
            Assert.NotEqual(roadTrip.Id, chill.Id);
            Assert.Equal(0, roadTrip.SongCount);
            Assert.Equal(2, (await store.GetPlaylistsAsync()).Count);

            Song a = MakeSong("s1");
            Song b = MakeSong("s2");
            Song c = MakeSong("s3");
            await store.AddSongToPlaylistAsync(roadTrip.Id, a);
            await store.AddSongToPlaylistAsync(roadTrip.Id, b);
            await store.AddSongToPlaylistAsync(roadTrip.Id, c);

            Assert.Equal(
                new[] { "s1", "s2", "s3" },
                (await store.GetPlaylistSongsAsync(roadTrip.Id)).Select(s => s.Id).ToArray());
            LibraryPlaylist listed = (await store.GetPlaylistsAsync())
                .Single(p => p.Id == roadTrip.Id);
            Assert.Equal(3, listed.SongCount);

            // Removal keeps the remaining order; a new add appends at the end.
            await store.RemoveSongFromPlaylistAsync(roadTrip.Id, "s2");
            await store.AddSongToPlaylistAsync(roadTrip.Id, MakeSong("s4"));
            Assert.Equal(
                new[] { "s1", "s3", "s4" },
                (await store.GetPlaylistSongsAsync(roadTrip.Id)).Select(s => s.Id).ToArray());

            // Re-adding an existing song moves it to the end (append semantics).
            await store.AddSongToPlaylistAsync(roadTrip.Id, a);
            Assert.Equal(
                new[] { "s3", "s4", "s1" },
                (await store.GetPlaylistSongsAsync(roadTrip.Id)).Select(s => s.Id).ToArray());

            // Playlists are independent.
            await store.AddSongToPlaylistAsync(chill.Id, a);
            Assert.Single(await store.GetPlaylistSongsAsync(chill.Id));

            await store.RenamePlaylistAsync(roadTrip.Id, "Road Trip 2026");
            Assert.Equal(
                "Road Trip 2026",
                (await store.GetPlaylistsAsync()).Single(p => p.Id == roadTrip.Id).Name);

            // Deleting a playlist deletes its memberships, not the other playlist's.
            await store.DeletePlaylistAsync(roadTrip.Id);
            Assert.Single(await store.GetPlaylistsAsync());
            Assert.Empty(await store.GetPlaylistSongsAsync(roadTrip.Id));
            Assert.Single(await store.GetPlaylistSongsAsync(chill.Id));
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task History_NewestFirst_DedupesReplays_AndClears()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);
            Song a = MakeSong("h1");
            Song b = MakeSong("h2");
            Song c = MakeSong("h3");

            await store.RecordPlayAsync(a);
            await store.RecordPlayAsync(b);
            await store.RecordPlayAsync(c);
            Assert.Equal(
                new[] { "h3", "h2", "h1" },
                (await store.GetHistoryAsync()).Select(s => s.Id).ToArray());

            // Re-playing moves the song to the top instead of duplicating it.
            await store.RecordPlayAsync(a);
            IReadOnlyList<Song> history = await store.GetHistoryAsync();
            Assert.Equal(new[] { "h1", "h3", "h2" }, history.Select(s => s.Id).ToArray());

            // Limit is honored.
            Assert.Equal(
                new[] { "h1", "h3" },
                (await store.GetHistoryAsync(limit: 2)).Select(s => s.Id).ToArray());

            await store.ClearHistoryAsync();
            Assert.Empty(await store.GetHistoryAsync());
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task Downloads_UpsertUpdatesInPlace_OrdersByUpdated_AndDeletes()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);
            var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

            var downloading = new DownloadRecord(
                SongId: "d1",
                Title: "Song One",
                Artists: "Artist One",
                ImageUrl: "https://c.saavncdn.com/430/X-150x150.jpg",
                LocalPath: null,
                Status: DownloadStatus.Downloading,
                Progress: 40,
                ErrorMessage: null,
                UpdatedAt: t0);
            await store.UpsertDownloadAsync(downloading);

            DownloadRecord stored = Assert.Single(await store.GetDownloadsAsync());
            Assert.Equal(downloading, stored);

            // Same song id: the row is replaced, not duplicated.
            DownloadRecord completed = downloading with
            {
                Status = DownloadStatus.Completed,
                Progress = 100,
                LocalPath = @"C:\Music\Song One.m4a",
                UpdatedAt = t0.AddMinutes(1),
            };
            await store.UpsertDownloadAsync(completed);
            DownloadRecord after = Assert.Single(await store.GetDownloadsAsync());
            Assert.Equal(completed, after);

            // Most recently updated first.
            await store.UpsertDownloadAsync(new DownloadRecord(
                SongId: "d2",
                Title: "Song Two",
                Artists: "Artist Two",
                ImageUrl: null,
                LocalPath: null,
                Status: DownloadStatus.Failed,
                Progress: 0,
                ErrorMessage: "HTTP 403",
                UpdatedAt: t0.AddMinutes(2)));
            Assert.Equal(
                new[] { "d2", "d1" },
                (await store.GetDownloadsAsync()).Select(r => r.SongId).ToArray());

            await store.DeleteDownloadAsync("d1");
            Assert.Equal(
                new[] { "d2" },
                (await store.GetDownloadsAsync()).Select(r => r.SongId).ToArray());
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task FavoriteEntities_SetGetRemove_RoundTrips_AndKindsAreIndependent()
    {
        string dbPath = NewTempDbPath();
        try
        {
            var store = new SqliteLibraryStore(dbPath);
            var album = new FavoriteEntity(
                Kind: "album",
                Id: "38682222",
                Title: "Bhediya",
                Subtitle: "Sachin-Jigar",
                ImageUrl: "https://c.saavncdn.com/001/Bhediya-Hindi-2022-500x500.jpg",
                Url: "https://www.jiosaavn.com/album/bhediya/38682222");
            var artist = new FavoriteEntity(
                Kind: "artist",
                Id: "459320",
                Title: "Arijit Singh",
                Subtitle: "Artist • 9503254 Listeners",
                ImageUrl: null,
                Url: null);
            // Same id as the album but a different kind: independent row.
            var playlist = new FavoriteEntity(
                Kind: "playlist",
                Id: "38682222",
                Title: "Bhediya Party Mix",
                Subtitle: null,
                ImageUrl: null,
                Url: null);

            Assert.Empty(await store.GetFavoriteEntitiesAsync());
            Assert.False(await store.IsFavoriteEntityAsync("album", album.Id));

            await store.SetFavoriteEntityAsync(album, isFavorite: true);
            await store.SetFavoriteEntityAsync(artist, isFavorite: true);
            await store.SetFavoriteEntityAsync(playlist, isFavorite: true);

            Assert.True(await store.IsFavoriteEntityAsync("album", "38682222"));
            Assert.True(await store.IsFavoriteEntityAsync("playlist", "38682222"));
            Assert.True(await store.IsFavoriteEntityAsync("artist", "459320"));
            Assert.False(await store.IsFavoriteEntityAsync("artist", "38682222"));

            // Most recently favorited first; full snapshot round-trips.
            IReadOnlyList<FavoriteEntity> favorites = await store.GetFavoriteEntitiesAsync();
            Assert.Equal(new[] { playlist, artist, album }, favorites);

            // Re-favoriting refreshes the snapshot but keeps the position.
            FavoriteEntity refreshed = album with { Title = "Bhediya (Deluxe)" };
            await store.SetFavoriteEntityAsync(refreshed, isFavorite: true);
            favorites = await store.GetFavoriteEntitiesAsync();
            Assert.Equal(3, favorites.Count);
            Assert.Equal(refreshed, favorites[2]);

            // Removal is by (kind, id): the playlist sharing the id stays.
            await store.SetFavoriteEntityAsync(album, isFavorite: false);
            Assert.False(await store.IsFavoriteEntityAsync("album", "38682222"));
            Assert.True(await store.IsFavoriteEntityAsync("playlist", "38682222"));
            Assert.Equal(
                new[] { playlist, artist },
                await store.GetFavoriteEntitiesAsync());

            // Survives a new store instance over the same file.
            var reopened = new SqliteLibraryStore(dbPath);
            Assert.Equal(
                new[] { playlist, artist },
                await reopened.GetFavoriteEntitiesAsync());
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }
}
