using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Playback;
using Omega.Services;

namespace Omega.ViewModels;

/// <summary>
/// Library (design §9.4): all-local data behind <see cref="ILibraryStore"/>
/// — Favourites / Playlists / Artists / Albums / Songs / History /
/// Downloads. Works fully offline. The Artists/Albums/Songs tabs are
/// aggregations over the local library UNION (favourites + every
/// playlist's songs + history + downloads, de-duped by song id):
/// snapshots are full-fidelity, so groups carry their upstream album /
/// artist ids and open the matching Detail page; an id-less group
/// instead filters the Songs tab. Playlist create/rename validation
/// lives in the page's dialogs (non-empty name); these methods assume
/// a validated name.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;
    private readonly DownloadService _downloadService;
    private bool _downloadUpdatesAttached;
    private List<Song> _unionSongs = new();
    private string? _filterKind;
    private string? _filterKey;

    public LibraryViewModel(IPlaybackGateway playback, ILibraryStore store, DownloadService downloadService)
    {
        _playback = playback;
        _store = store;
        _downloadService = downloadService;
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    public ObservableCollection<SongItemViewModel> Favorites { get; } = new();

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    public ObservableCollection<SongItemViewModel> PlaylistSongs { get; } = new();

    public ObservableCollection<LibraryGroupItemViewModel> Artists { get; } = new();

    public ObservableCollection<LibraryGroupItemViewModel> Albums { get; } = new();

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    public ObservableCollection<SongItemViewModel> History { get; } = new();

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial PlaylistItemViewModel? SelectedPlaylist { get; set; }

    public bool HasSelectedPlaylist => SelectedPlaylist is not null;

    public bool HasFavorites => Favorites.Count > 0;

    public bool HasPlaylists => Playlists.Count > 0;

    public bool HasArtists => Artists.Count > 0;

    public bool HasAlbums => Albums.Count > 0;

    public bool HasSongs => Songs.Count > 0;

    public bool HasHistory => History.Count > 0;

    public bool HasDownloads => Downloads.Count > 0;

    public string FavoritesCountText => Res.Format("FavoritesCountFormat", Favorites.Count);

    public string PlaylistsCountText => Res.Format("PlaylistsCountFormat", Playlists.Count);

    public string ArtistsCountText => Res.Format("ArtistsCountFormat", Artists.Count);

    public string AlbumsCountText => Res.Format("AlbumsCountFormat", Albums.Count);

    public string SongsCountText => Res.Format("SongsCountFormat", Songs.Count);

    public string HistoryCountText => Res.Format("HistoryCountFormat", History.Count);

    public string DownloadsCountText => Res.Format("DownloadsCountFormat", Downloads.Count);

    /// <summary>True while the Songs tab is filtered to one artist/album group.</summary>
    public bool HasSongsFilter => _filterKey is not null;

    public string SongsFilterText
    {
        get
        {
            LibraryGroupItemViewModel? group = CurrentFilterGroup();
            if (group is null)
            {
                return string.Empty;
            }

            return Res.Format(
                group.Kind == "artist" ? "SongsFilterArtistFormat" : "SongsFilterAlbumFormat",
                group.Name);
        }
    }

    /// <summary>
    /// Loads every section independently (audit M3): one failing
    /// section reports into the page error bar but never blanks the
    /// others — each load keeps whatever data it already had or
    /// fills its own collection, and the aggregations rebuild from
    /// whatever the store could return.
    /// </summary>
    public async Task LoadAllAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        var failures = new List<string>();
        try
        {
            try
            {
                await _store.InitializeAsync();
            }
            catch (Exception ex)
            {
                // The store also initialises lazily per call, so a
                // warm-up failure must not take the sections down.
                failures.Add(ex.Message);
            }

            await RunSectionAsync(LoadFavoritesAsync, failures);
            await RunSectionAsync(LoadPlaylistsAsync, failures);
            await RunSectionAsync(LoadHistoryAsync, failures);
            await RunSectionAsync(LoadDownloadsAsync, failures);
            await RunSectionAsync(RebuildAggregationsAsync, failures);
        }
        finally
        {
            IsLoading = false;
            ErrorMessage = failures.Count > 0 ? string.Join("\n", failures) : null;
            NotifyCounts();
        }
    }

    private static async Task RunSectionAsync(Func<Task> section, List<string> failures)
    {
        try
        {
            await section();
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAllAsync();

    // ----------------------------------------------------------------
    // Download progress wiring (page attaches while visible)
    // ----------------------------------------------------------------

    /// <summary>Subscribes to live download-record updates (idempotent).</summary>
    public void AttachDownloadUpdates()
    {
        if (_downloadUpdatesAttached)
        {
            return;
        }

        _downloadUpdatesAttached = true;
        _downloadService.RecordChanged += OnDownloadRecordChanged;
    }

    /// <summary>Unsubscribes from live download-record updates.</summary>
    public void DetachDownloadUpdates()
    {
        if (!_downloadUpdatesAttached)
        {
            return;
        }

        _downloadUpdatesAttached = false;
        _downloadService.RecordChanged -= OnDownloadRecordChanged;
    }

    private void OnDownloadRecordChanged(object? sender, DownloadRecord record)
    {
        // DownloadService raises this on the UI thread.
        var item = new DownloadItemViewModel(record, DeleteDownloadAsync);
        for (int i = 0; i < Downloads.Count; i++)
        {
            if (Downloads[i].Record.SongId == record.SongId)
            {
                Downloads[i] = item;
                NotifyCounts();
                return;
            }
        }

        Downloads.Insert(0, item); // store order is most-recently-updated first
        NotifyCounts();
    }

    // ----------------------------------------------------------------
    // Favourites
    // ----------------------------------------------------------------

    private async Task LoadFavoritesAsync()
    {
        IReadOnlyList<Song> favorites = await _store.GetFavoritesAsync();
        Favorites.Clear();
        foreach (Song song in favorites)
        {
            SongItemViewModel row = MakeRow(
                song,
                () => Favorites.Select(r => r.Song).ToList(),
                favoriteChanged: item =>
                {
                    if (!item.IsFavorite)
                    {
                        Favorites.Remove(item);
                        NotifyCounts();
                        _ = RebuildAggregationsAsync();
                    }
                });
            row.IsFavorite = true; // it came from the favourites table
            Favorites.Add(row);
        }
    }

    // ----------------------------------------------------------------
    // Playlists
    // ----------------------------------------------------------------

    private async Task LoadPlaylistsAsync()
    {
        IReadOnlyList<LibraryPlaylist> playlists = await _store.GetPlaylistsAsync();
        string? keepId = SelectedPlaylist?.Id;
        Playlists.Clear();
        foreach (LibraryPlaylist playlist in playlists)
        {
            Playlists.Add(new PlaylistItemViewModel(playlist));
        }

        SelectedPlaylist =
            Playlists.FirstOrDefault(p => p.Id == keepId) ?? Playlists.FirstOrDefault();
        if (SelectedPlaylist is null)
        {
            PlaylistSongs.Clear();
        }
    }

    /// <summary>Creates a playlist (name already validated by the dialog) and selects it.</summary>
    public async Task CreatePlaylistAsync(string name)
    {
        LibraryPlaylist created = await _store.CreatePlaylistAsync(name);
        await LoadPlaylistsAsync();
        SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == created.Id);
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    /// <summary>Renames the selected playlist (name already validated by the dialog).</summary>
    public async Task RenameSelectedPlaylistAsync(string name)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        await _store.RenamePlaylistAsync(SelectedPlaylist.Id, name);
        await LoadPlaylistsAsync();
        NotifyCounts();
    }

    /// <summary>Deletes the selected playlist (page confirms first).</summary>
    public async Task DeleteSelectedPlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        await _store.DeletePlaylistAsync(SelectedPlaylist.Id);
        SelectedPlaylist = null;
        PlaylistSongs.Clear();
        await LoadPlaylistsAsync();
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    private async Task LoadPlaylistSongsAsync(string playlistId)
    {
        IReadOnlyList<Song> songs = await _store.GetPlaylistSongsAsync(playlistId);
        PlaylistSongs.Clear();
        foreach (Song song in songs)
        {
            SongItemViewModel row = MakeRow(
                song,
                () => PlaylistSongs.Select(r => r.Song).ToList(),
                removeHandler: item => RemoveSongFromSelectedPlaylistAsync(item));
            PlaylistSongs.Add(row);
            await row.RefreshFavoriteAsync();
        }
    }

    private async Task RemoveSongFromSelectedPlaylistAsync(SongItemViewModel item)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        await _store.RemoveSongFromPlaylistAsync(SelectedPlaylist.Id, item.Song.Id);
        PlaylistSongs.Remove(item);
        await LoadPlaylistsAsync(); // refresh song counts
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    [RelayCommand]
    private async Task PlaySelectedPlaylistAsync()
    {
        IReadOnlyList<Song> queue = PlaylistSongs.Select(r => r.Song).ToList();
        if (queue.Count > 0)
        {
            await PlaySongAsync(queue[0], queue);
        }
    }

    // ----------------------------------------------------------------
    // Artists / Albums / Songs aggregations
    // ----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the library union (favourites → playlist songs →
    /// history → downloads, first snapshot wins per song id) and the
    /// artist/album groups + Songs rows derived from it. Fetched
    /// straight from the store so it does not depend on which section
    /// loads succeeded.
    /// </summary>
    private async Task RebuildAggregationsAsync()
    {
        var byId = new Dictionary<string, Song>(StringComparer.Ordinal);
        void Add(Song song)
        {
            if (!byId.ContainsKey(song.Id))
            {
                byId.Add(song.Id, song);
            }
        }

        foreach (Song song in await _store.GetFavoritesAsync())
        {
            Add(song);
        }

        foreach (LibraryPlaylist playlist in await _store.GetPlaylistsAsync())
        {
            foreach (Song song in await _store.GetPlaylistSongsAsync(playlist.Id))
            {
                Add(song);
            }
        }

        foreach (Song song in await _store.GetHistoryAsync(500))
        {
            Add(song);
        }

        foreach (DownloadRecord record in await _store.GetDownloadsAsync())
        {
            Add(SongFromRecord(record));
        }

        _unionSongs = byId.Values.ToList();
        RebuildGroups();
        await RebuildSongRowsAsync();
        NotifyCounts();
    }

    private void RebuildGroups()
    {
        var artistGroups = new Dictionary<string, (ArtistRef Artist, List<Song> Songs)>(
            StringComparer.OrdinalIgnoreCase);
        var albumGroups = new Dictionary<string, (string Name, string? AlbumId, List<Song> Songs)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (Song song in _unionSongs)
        {
            ArtistRef? primary = song.Artists.Primary.Count > 0
                ? song.Artists.Primary[0]
                : song.Artists.All.Count > 0 ? song.Artists.All[0] : null;
            if (primary is not null && primary.Name.Length > 0)
            {
                string key = primary.Id.Length > 0 ? "id:" + primary.Id : "name:" + primary.Name;
                if (!artistGroups.TryGetValue(key, out (ArtistRef Artist, List<Song> Songs) group))
                {
                    group = (primary, new List<Song>());
                    artistGroups.Add(key, group);
                }

                group.Songs.Add(song);
            }

            bool hasAlbumIdentity = !string.IsNullOrEmpty(song.AlbumId) || !string.IsNullOrWhiteSpace(song.AlbumName);
            if (hasAlbumIdentity)
            {
                string key = !string.IsNullOrEmpty(song.AlbumId)
                    ? "id:" + song.AlbumId
                    : "name:" + song.AlbumName!.Trim() + "|" + (primary?.Name ?? string.Empty);
                if (!albumGroups.TryGetValue(key, out (string Name, string? AlbumId, List<Song> Songs) group))
                {
                    group = (song.AlbumName ?? song.AlbumId!, song.AlbumId, new List<Song>());
                    albumGroups.Add(key, group);
                }

                group.Songs.Add(song);
            }
        }

        Artists.Clear();
        foreach ((string key, (ArtistRef artist, List<Song> groupSongs)) in artistGroups
            .OrderByDescending(pair => pair.Value.Songs.Count)
            .ThenBy(pair => pair.Value.Artist.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            DetailNavigationArgs? args = artist.Id.Length > 0
                ? DetailNavigationArgs.Artist(artist.Id)
                : null;
            string? artwork = artist.Image.Best
                ?? groupSongs.Select(s => s.Image.Best).FirstOrDefault(u => u is not null);
            Artists.Add(new LibraryGroupItemViewModel(
                "artist", key, artist.Name,
                Res.Format("PlaylistSongCountFormat", groupSongs.Count),
                artwork, args, groupSongs));
        }

        Albums.Clear();
        foreach ((string key, (string name, string? albumId, List<Song> groupSongs)) in albumGroups
            .OrderByDescending(pair => pair.Value.Songs.Count)
            .ThenBy(pair => pair.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            DetailNavigationArgs? args = albumId is { Length: > 0 }
                ? DetailNavigationArgs.Album(albumId)
                : null;
            Song first = groupSongs[0];
            string artistsText = first.PrimaryArtistNames;
            string countText = Res.Format("PlaylistSongCountFormat", groupSongs.Count);
            string subtitle = artistsText.Length > 0 ? artistsText + " · " + countText : countText;
            string? artwork = groupSongs.Select(s => s.Image.Best).FirstOrDefault(u => u is not null);
            Albums.Add(new LibraryGroupItemViewModel(
                "album", key, name, subtitle, artwork, args, groupSongs));
        }
    }

    /// <summary>Filters the Songs tab to one group (the page switches tabs after calling this).</summary>
    public async Task ApplyGroupFilterAsync(LibraryGroupItemViewModel group)
    {
        _filterKind = group.Kind;
        _filterKey = group.Key;
        await RebuildSongRowsAsync();
        OnPropertyChanged(nameof(HasSongsFilter));
        OnPropertyChanged(nameof(SongsFilterText));
    }

    [RelayCommand]
    private async Task ClearSongsFilterAsync()
    {
        _filterKind = null;
        _filterKey = null;
        await RebuildSongRowsAsync();
        OnPropertyChanged(nameof(HasSongsFilter));
        OnPropertyChanged(nameof(SongsFilterText));
    }

    private LibraryGroupItemViewModel? CurrentFilterGroup()
    {
        if (_filterKind is null || _filterKey is null)
        {
            return null;
        }

        ObservableCollection<LibraryGroupItemViewModel> source =
            _filterKind == "artist" ? Artists : Albums;
        foreach (LibraryGroupItemViewModel group in source)
        {
            if (group.Key == _filterKey)
            {
                return group;
            }
        }

        return null;
    }

    private async Task RebuildSongRowsAsync()
    {
        LibraryGroupItemViewModel? group = CurrentFilterGroup();
        if (_filterKey is not null && group is null)
        {
            // The filtered group vanished (its last song left the
            // library) — fall back to the full union rather than an
            // unexplained empty tab.
            _filterKind = null;
            _filterKey = null;
            OnPropertyChanged(nameof(HasSongsFilter));
            OnPropertyChanged(nameof(SongsFilterText));
        }

        IReadOnlyList<Song> source = group is not null ? group.Songs : _unionSongs;
        Songs.Clear();
        foreach (Song song in source)
        {
            SongItemViewModel row = MakeRow(song, () => Songs.Select(r => r.Song).ToList());
            Songs.Add(row);
            await row.RefreshFavoriteAsync();
        }
    }

    // ----------------------------------------------------------------
    // History
    // ----------------------------------------------------------------

    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<Song> history = await _store.GetHistoryAsync(100);
        History.Clear();
        foreach (Song song in history)
        {
            SongItemViewModel row = MakeRow(song, () => History.Select(r => r.Song).ToList());
            History.Add(row);
            await row.RefreshFavoriteAsync();
        }
    }

    /// <summary>Clears listening history (page confirms first).</summary>
    public async Task ClearHistoryAsync()
    {
        await _store.ClearHistoryAsync();
        History.Clear();
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    // ----------------------------------------------------------------
    // Downloads
    // ----------------------------------------------------------------

    private async Task LoadDownloadsAsync()
    {
        IReadOnlyList<DownloadRecord> downloads = await _store.GetDownloadsAsync();
        Downloads.Clear();
        foreach (DownloadRecord record in downloads)
        {
            Downloads.Add(new DownloadItemViewModel(record, DeleteDownloadAsync));
        }
    }

    private async Task DeleteDownloadAsync(DownloadItemViewModel item)
    {
        await _downloadService.DeleteAsync(item.Record); // local file + record
        Downloads.Remove(item);
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    /// <summary>
    /// Row Download action: runs the transfer through
    /// <see cref="DownloadService"/> (progress lands in the Downloads
    /// tab via RecordChanged while the page is attached), then
    /// refreshes the tab and the aggregations.
    /// </summary>
    private async Task DownloadSongAsync(Song song)
    {
        try
        {
            await _downloadService.DownloadAsync(song);
        }
        catch (Exception)
        {
            // DownloadAsync reports failure through the record by
            // contract; this guard keeps a service surprise from
            // faulting the row command.
        }

        await LoadDownloadsAsync();
        await RebuildAggregationsAsync();
        NotifyCounts();
    }

    /// <summary>
    /// Downloads-tab row activation: completed downloads play from
    /// their local file (queue = every completed download, tab
    /// order); failed rows retry via upstream re-resolution; a
    /// still-downloading row is a no-op.
    /// </summary>
    public async Task PlayDownloadAsync(DownloadItemViewModel item)
    {
        if (item.Record.Status == DownloadStatus.Failed)
        {
            await _downloadService.RetryAsync(item.Record.SongId);
            await LoadDownloadsAsync();
            await RebuildAggregationsAsync();
            NotifyCounts();
            return;
        }

        if (item.Record.Status != DownloadStatus.Completed)
        {
            return;
        }

        var queue = new List<Song>();
        Song? start = null;
        foreach (DownloadItemViewModel row in Downloads)
        {
            if (row.Record.Status != DownloadStatus.Completed)
            {
                continue;
            }

            Song song = SongFromRecord(row.Record);
            queue.Add(song);
            if (row.Record.SongId == item.Record.SongId)
            {
                start = song;
            }
        }

        if (start is not null && queue.Count > 0)
        {
            await PlaySongAsync(start, queue);
        }
    }

    /// <summary>
    /// Rebuilds a playable song from a download record (records carry
    /// display metadata only). Completed downloads get a single-rung
    /// ladder pointing at the local file — <see cref="PlayerService"/>
    /// feeds ladder URLs straight to MediaSource.CreateFromUri, so a
    /// file URI plays from disk; if the file is gone, the engine's
    /// failure ladder re-resolves the song upstream by id. Incomplete
    /// records get an empty ladder, which the engine also re-resolves
    /// at play time.
    /// </summary>
    private static Song SongFromRecord(DownloadRecord record)
    {
        var artists = new ArtistGroups(
            new[] { new ArtistRef(string.Empty, record.Artists, null, ImageSet.Empty, null) },
            Array.Empty<ArtistRef>(),
            Array.Empty<ArtistRef>());
        IReadOnlyList<QualityUrl> ladder =
            record is { Status: DownloadStatus.Completed, LocalPath: { } path }
                ? new[] { new QualityUrl("Downloaded", 0, new Uri(path).AbsoluteUri) }
                : Array.Empty<QualityUrl>();
        return new Song(
            Id: record.SongId,
            Name: record.Title,
            Subtitle: null,
            Url: null,
            Image: new ImageSet(record.ImageUrl, record.ImageUrl, record.ImageUrl),
            Language: null,
            Year: null,
            PlayCount: null,
            Explicit: false,
            DurationSeconds: null,
            ReleaseDate: null,
            Label: null,
            HasLyrics: false,
            Copyright: null,
            AlbumId: null,
            AlbumName: null,
            AlbumUrl: null,
            Artists: artists,
            StreamUrls: ladder,
            Is320Kbps: false);
    }

    // ----------------------------------------------------------------
    // Shared
    // ----------------------------------------------------------------

    private SongItemViewModel MakeRow(
        Song song,
        Func<IReadOnlyList<Song>> queueProvider,
        Func<SongItemViewModel, Task>? removeHandler = null,
        Action<SongItemViewModel>? favoriteChanged = null) =>
        new(
            song,
            _store,
            s => PlaySongAsync(s, queueProvider()),
            s => AddToPlaylistRequested?.Invoke(s),
            removeHandler,
            favoriteChanged,
            s => DownloadSongAsync(s));

    private async Task PlaySongAsync(Song song, IReadOnlyList<Song> queue)
    {
        await _playback.PlayAsync(song, queue);
        try
        {
            await _store.RecordPlayAsync(song);
        }
        catch (Exception)
        {
            // History is best-effort; playback already started.
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasArtists));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(FavoritesCountText));
        OnPropertyChanged(nameof(PlaylistsCountText));
        OnPropertyChanged(nameof(ArtistsCountText));
        OnPropertyChanged(nameof(AlbumsCountText));
        OnPropertyChanged(nameof(SongsCountText));
        OnPropertyChanged(nameof(HistoryCountText));
        OnPropertyChanged(nameof(DownloadsCountText));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedPlaylist));
        if (value is not null)
        {
            _ = LoadPlaylistSongsAsync(value.Id);
        }
        else
        {
            PlaylistSongs.Clear();
        }
    }
}

/// <summary>
/// One artist/album group tile on the Library aggregation tabs:
/// a name, a "N songs" (albums: "Artist · N songs") subtitle, cover
/// artwork, and the group's songs. When the group carries an upstream
/// id, <see cref="DetailArgs"/> opens the matching Detail page;
/// otherwise the page filters the Songs tab to <see cref="Songs"/>.
/// </summary>
public sealed class LibraryGroupItemViewModel
{
    public LibraryGroupItemViewModel(
        string kind,
        string key,
        string name,
        string subtitle,
        string? imageUrl,
        DetailNavigationArgs? detailArgs,
        IReadOnlyList<Song> songs)
    {
        Kind = kind;
        Key = key;
        Name = name;
        Subtitle = subtitle;
        Artwork = ArtworkHelper.From(imageUrl);
        DetailArgs = detailArgs;
        Songs = songs;
    }

    /// <summary>"artist" or "album".</summary>
    public string Kind { get; }

    /// <summary>Stable group identity ("id:…" or "name:…") — the Songs-filter key.</summary>
    public string Key { get; }

    public string Name { get; }

    public string Subtitle { get; }

    public ImageSource? Artwork { get; }

    /// <summary>Detail navigation for id-carrying groups; null → Songs-tab filter.</summary>
    public DetailNavigationArgs? DetailArgs { get; }

    public IReadOnlyList<Song> Songs { get; }
}
