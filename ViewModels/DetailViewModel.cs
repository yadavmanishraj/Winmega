using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Playback;
using Omega.Core.Upstream;
using Omega.Services;

namespace Omega.ViewModels;

/// <summary>
/// Detail page (design §9.5): one page for Album / Playlist / Artist,
/// chosen by the <see cref="DetailNavigationArgs"/> kind. Albums load
/// their full track list in one call; playlists page upstream until
/// <c>list_count</c> is accumulated (handled by the client); artists
/// show top songs/albums/singles + similar artists, with paged
/// "load more" for songs.
/// </summary>
public partial class DetailViewModel : ObservableObject
{
    private readonly JioSaavnClient _client;
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;
    private readonly DownloadService _downloadService;
    private CancellationTokenSource? _loadCts;
    private int _artistSongsNextPage;
    private int _artistSongsTotal;

    public DetailViewModel(JioSaavnClient client, IPlaybackGateway playback, ILibraryStore store, DownloadService downloadService)
    {
        _client = client;
        _playback = playback;
        _store = store;
        _downloadService = downloadService;
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    public ObservableCollection<SongItemViewModel> Singles { get; } = new();

    public ObservableCollection<TileItemViewModel> TopAlbums { get; } = new();

    public ObservableCollection<TileItemViewModel> SimilarArtists { get; } = new();

    /// <summary>The navigation kind: "album" | "playlist" | "artist" (empty before load).</summary>
    public string Kind { get; private set; } = string.Empty;

    public bool IsArtist => Kind == "artist";

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BioText { get; set; } = string.Empty;

    public bool HasBio => BioText.Length > 0;

    public ImageSource? HeaderArtwork { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool HasMoreSongs { get; set; }

    public bool HasSongs => Songs.Count > 0;

    public bool HasSingles => Singles.Count > 0;

    public bool HasTopAlbums => TopAlbums.Count > 0;

    public bool HasSimilarArtists => SimilarArtists.Count > 0;

    /// <summary>Cancels an in-flight load (page navigated away).</summary>
    public void CancelLoads() => _loadCts?.Cancel();

    public async Task LoadAsync(DetailNavigationArgs args)
    {
        _lastArgs = args;
        Kind = args.Kind;
        OnPropertyChanged(nameof(IsArtist));
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        CancellationToken ct = _loadCts.Token;

        IsLoading = true;
        ErrorMessage = null;
        Songs.Clear();
        Singles.Clear();
        TopAlbums.Clear();
        SimilarArtists.Clear();
        HasMoreSongs = false;
        _artistSongsNextPage = 0;
        _artistSongsTotal = 0;

        try
        {
            try
            {
                await _store.InitializeAsync();
            }
            catch (Exception)
            {
                // Local data degrades (favourites/history); the
                // upstream detail content must still load.
            }

            switch (args.Kind)
            {
                case "album":
                    await LoadAlbumAsync(args.Id, ct);
                    break;
                case "playlist":
                    await LoadPlaylistAsync(args.Id, ct);
                    break;
                case "artist":
                    await LoadArtistAsync(args.Id, ct);
                    break;
                default:
                    ErrorMessage = Res.Get("DetailUnknownKind");
                    break;
            }

            foreach (SongItemViewModel row in Songs.Concat(Singles))
            {
                await row.RefreshFavoriteAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyContentVisibility();
        }
    }

    private DetailNavigationArgs? _lastArgs;

    [RelayCommand]
    private Task RetryAsync() =>
        _lastArgs is null ? Task.CompletedTask : LoadAsync(_lastArgs);

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        IReadOnlyList<Song> queue = Songs.Select(r => r.Song).ToList();
        if (queue.Count > 0)
        {
            await PlaySongAsync(queue[0], queue);
        }
    }

    /// <summary>Artist songs paging (accumulation rule — see SearchViewModel).</summary>
    [RelayCommand]
    private async Task LoadMoreSongsAsync()
    {
        if (!IsArtist || !HasMoreSongs)
        {
            return;
        }

        if (_currentArtistId is not string artistId)
        {
            return;
        }

        PagedResult<Song> page = await _client.GetArtistSongsAsync(artistId, _artistSongsNextPage);
        _artistSongsTotal = page.Total;
        IReadOnlyList<Song> queueSnapshot = Songs.Select(r => r.Song).Concat(page.Items).ToList();
        foreach (Song song in page.Items)
        {
            SongItemViewModel row = MakeRow(song, () => queueSnapshot);
            Songs.Add(row);
            await row.RefreshFavoriteAsync();
        }

        _artistSongsNextPage++;
        if (page.Items.Count == 0)
        {
            _artistSongsTotal = Songs.Count;
        }

        HasMoreSongs = Songs.Count < _artistSongsTotal;
        NotifyContentVisibility();
    }

    private string? _currentArtistId;

    private async Task LoadAlbumAsync(string albumId, CancellationToken ct)
    {
        Album album = await _client.GetAlbumAsync(albumId, ct);
        Title = album.Name;
        HeaderArtwork = ArtworkHelper.From(album.Image.Large ?? album.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));
        MetaText = JoinMeta(
            album.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            album.Language,
            Res.Format("PlaylistSongCountFormat", album.SongCount));
        foreach (Song song in album.Songs)
        {
            Songs.Add(MakeRow(song, () => album.Songs));
        }
    }

    private async Task LoadPlaylistAsync(string playlistId, CancellationToken ct)
    {
        Playlist playlist = await _client.GetPlaylistWithAllSongsAsync(playlistId, cancellationToken: ct);
        Title = playlist.Name;
        HeaderArtwork = ArtworkHelper.From(playlist.Image.Large ?? playlist.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));
        MetaText = JoinMeta(
            playlist.OwnerName,
            playlist.Language,
            Res.Format("PlaylistSongCountFormat", playlist.SongCount));
        IReadOnlyList<Song> all = playlist.Songs;
        foreach (Song song in all)
        {
            Songs.Add(MakeRow(song, () => all));
        }
    }

    private async Task LoadArtistAsync(string artistId, CancellationToken ct)
    {
        _currentArtistId = artistId;
        Artist artist = await _client.GetArtistAsync(artistId, cancellationToken: ct);
        Title = artist.Name;
        HeaderArtwork = ArtworkHelper.From(artist.Image.Large ?? artist.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));

        string followers = DisplayFormatting.Count(artist.FollowerCount);
        MetaText = JoinMeta(
            followers.Length > 0 ? Res.Format("ArtistFollowersFormat", followers) : null,
            artist.DominantLanguage);

        BioText = string.Join(
            "\n\n",
            artist.Bio.Where(b => !string.IsNullOrWhiteSpace(b.Text)).Select(b => b.Text!.Trim()));
        OnPropertyChanged(nameof(HasBio));

        IReadOnlyList<Song> topSongs = artist.TopSongs;
        foreach (Song song in topSongs)
        {
            Songs.Add(MakeRow(song, () => topSongs));
        }

        IReadOnlyList<Song> singles = artist.Singles;
        foreach (Song song in singles)
        {
            Singles.Add(MakeRow(song, () => singles));
        }

        foreach (Album album in artist.TopAlbums)
        {
            TopAlbums.Add(TileItemViewModel.FromAlbum(album));
        }

        foreach (ArtistRef similar in artist.SimilarArtists)
        {
            SimilarArtists.Add(TileItemViewModel.FromArtistRef(similar));
        }

        // The artist page payload embeds the first top-songs page and
        // carries NO total, so "load more" is enabled when that page
        // came back full (the client asks for 10); further pages come
        // from artist.getArtistMoreSong, and an empty page terminates
        // paging (accumulation rule). _artistSongsTotal int.MaxValue
        // means "unknown — rely on the empty-page stop".
        _artistSongsTotal = int.MaxValue;
        _artistSongsNextPage = 1;
        HasMoreSongs = topSongs.Count >= 10;
    }

    private SongItemViewModel MakeRow(Song song, Func<IReadOnlyList<Song>> queueProvider) =>
        new(song, _store, s => PlaySongAsync(s, queueProvider()), s => AddToPlaylistRequested?.Invoke(s),
            playNextHandler: s => { _ = _playback.PlayNextAsync(s); },
            addToQueueHandler: s => { _ = _playback.EnqueueAsync(s); },
            downloadHandler: s => _downloadService.DownloadAsync(s));

    /// <summary>True when the header offers "download all" (album / playlist track lists).</summary>
    public bool CanDownloadAll => (Kind == "album" || Kind == "playlist") && Songs.Count > 0;

    /// <summary>Downloads every track of the album/playlist, sequentially (FX3).</summary>
    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        foreach (SongItemViewModel row in Songs)
        {
            await _downloadService.DownloadAsync(row.Song);
        }
    }

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

    private static string JoinMeta(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void NotifyContentVisibility()
    {
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasSingles));
        OnPropertyChanged(nameof(HasTopAlbums));
        OnPropertyChanged(nameof(HasSimilarArtists));
        OnPropertyChanged(nameof(CanDownloadAll));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnBioTextChanged(string value) => OnPropertyChanged(nameof(HasBio));
}
