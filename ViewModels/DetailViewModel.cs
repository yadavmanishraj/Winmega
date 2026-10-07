using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using Windows.ApplicationModel.DataTransfer;

namespace Omega.ViewModels;

/// <summary>
/// One artist link in an album header's byline (name + artist id).
/// Immutable — compiled bindings only read it.
/// </summary>
public sealed record DetailArtistLink(string Id, string Name);

/// <summary>One titled paragraph of an artist's About section.</summary>
public sealed record DetailBioSection(string? Title, string Text);

/// <summary>
/// A chart row: the rank numeral plus the shared song row it belongs
/// to (DETAIL_PAGE_DESIGN §1.2 — position on a chart IS the rank;
/// upstream carries no movement data and none is faked).
/// </summary>
public sealed class RankedSongRow
{
    public RankedSongRow(int rank, SongItemViewModel row)
    {
        Rank = rank;
        Row = row;
    }

    public int Rank { get; }

    public SongItemViewModel Row { get; }
}

/// <summary>
/// Detail page (DETAIL_PAGE_DESIGN, approved 2026-10-07): one page for
/// Album / Playlist (incl. charts) / Artist, chosen by the
/// <see cref="DetailNavigationArgs"/> kind. The header paints instantly
/// from the args' preview fields; the detail load fills in behind it.
/// Albums load their full track list in one call; playlists page
/// upstream until <c>list_count</c> is accumulated (handled by the
/// client); artists show top songs (revealed in chunks of five, then
/// paged), latest release, album/singles bands, dedicated + featured
/// playlist bands, an About section and similar artists — each
/// follow-up section under its own guard so one failure never blanks
/// the page.
/// </summary>
public partial class DetailViewModel : ObservableObject
{
    /// <summary>Top songs are revealed five at a time (design §1.3).</summary>
    private const int TopSongsChunk = 5;

    /// <summary>
    /// The description's More/Less toggle appears when the text is
    /// long enough to plausibly overflow the 3-line clamp at reading
    /// width (~75 chars/line at 760px). A character gate is used
    /// instead of measuring the laid-out text — simpler and stable
    /// across window sizes; the cost is an occasional toggle on a
    /// description that would have fit.
    /// </summary>
    private const int DescriptionToggleThreshold = 220;

    private readonly JioSaavnClient _client;
    private readonly IPlaybackGateway _playback;
    private readonly PlayerService _player;
    private readonly ILibraryStore _store;
    private readonly DownloadService _downloadService;
    private CancellationTokenSource? _loadCts;
    private DetailNavigationArgs? _lastArgs;

    // Resolved entity identity + the snapshot an entity favourite
    // persists (kept current as the detail load lands).
    private string _resolvedId = string.Empty;
    private string? _entityUrl;
    private string? _favoriteSubtitle;
    private string? _favoriteImageUrl;

    // Artist top-songs paging: the master list holds every top song
    // known so far (embedded page + fetched pages); the visible Songs
    // collection reveals it in chunks, and rows queue against the
    // master list so Play All / row play see everything loaded.
    private readonly List<Song> _topSongsMaster = new();
    private int _topSongsRevealed;
    private int _artistSongsNextPage;
    private bool _topSongsEnded = true;

    public DetailViewModel(
        JioSaavnClient client,
        IPlaybackGateway playback,
        PlayerService player,
        ILibraryStore store,
        DownloadService downloadService)
    {
        _client = client;
        _playback = playback;
        _player = player;
        _store = store;
        _downloadService = downloadService;
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    /// <summary>Track rows (album/playlist/chart songs; the revealed chunk of artist top songs).</summary>
    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>The same rows with chart ranks (filled only for chart playlists).</summary>
    public ObservableCollection<RankedSongRow> RankedSongs { get; } = new();

    /// <summary>Album byline artist links (albums only).</summary>
    public ObservableCollection<DetailArtistLink> AlbumArtists { get; } = new();

    /// <summary>Artist singles &amp; EPs — album-lite items, rendered as album cards.</summary>
    public ObservableCollection<TileItemViewModel> Singles { get; } = new();

    public ObservableCollection<TileItemViewModel> TopAlbums { get; } = new();

    public ObservableCollection<TileItemViewModel> SimilarArtists { get; } = new();

    public ObservableCollection<TileItemViewModel> DedicatedPlaylists { get; } = new();

    public ObservableCollection<TileItemViewModel> FeaturedPlaylists { get; } = new();

    public ObservableCollection<TileItemViewModel> MoreByAlbums { get; } = new();

    public ObservableCollection<DetailBioSection> BioSections { get; } = new();

    public ObservableCollection<string> AboutFacts { get; } = new();

    /// <summary>The navigation kind: "album" | "playlist" | "artist" (empty before load).</summary>
    public string Kind { get; private set; } = string.Empty;

    public bool IsArtist => Kind == "artist";

    /// <summary>True when the caller marked this playlist as a chart (ranked rows + CHART eyebrow).</summary>
    public bool IsChart { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EyebrowText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MetaText { get; set; } = string.Empty;

    /// <summary>Playlist owner line (the byline slot for playlists).</summary>
    [ObservableProperty]
    public partial string BylineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDescriptionExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsVerifiedArtist { get; set; }

    [ObservableProperty]
    public partial string ProvenanceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MoreByHeaderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DedicatedHeaderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TileItemViewModel? LatestRelease { get; set; }

    [ObservableProperty]
    public partial Uri? WikiUrl { get; set; }

    public ImageSource? HeaderArtwork { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool IsContentLoaded { get; set; }

    [ObservableProperty]
    public partial bool HasMoreSongs { get; set; }

    [ObservableProperty]
    public partial bool IsFavoriteEntity { get; set; }

    [ObservableProperty]
    public partial bool IsShuffleOn { get; set; }

    public bool HasSongs => Songs.Count > 0;

    public bool HasAlbumArtists => AlbumArtists.Count > 0;

    public bool HasByline => Kind == "playlist" && !string.IsNullOrWhiteSpace(BylineText);

    public bool HasDescription => Description.Length > 0;

    public bool ShowDescriptionToggle => Description.Length > DescriptionToggleThreshold;

    /// <summary>0 = unlimited (WinUI TextBlock semantics) once expanded.</summary>
    public int DescriptionMaxLines => IsDescriptionExpanded ? 0 : 3;

    public string DescriptionToggleText =>
        IsDescriptionExpanded ? Res.Get("LessText") : Res.Get("MoreText");

    public bool HasSingles => Singles.Count > 0;

    public bool HasTopAlbums => TopAlbums.Count > 0;

    public bool HasSimilarArtists => SimilarArtists.Count > 0;

    public bool HasDedicatedPlaylists => DedicatedPlaylists.Count > 0;

    public bool HasFeaturedPlaylists => FeaturedPlaylists.Count > 0;

    public bool HasMoreBy => MoreByAlbums.Count > 0;

    public bool HasLatestRelease => LatestRelease is not null;

    public bool HasBioSections => BioSections.Count > 0;

    public bool HasWiki => WikiUrl is not null;

    public bool HasAbout => HasBioSections || AboutFacts.Count > 0 || HasWiki;

    public bool HasProvenance => ProvenanceText.Length > 0;

    /// <summary>Zero-song album/playlist after a completed load: header + composed empty state.</summary>
    public bool ShowEmptyState =>
        IsContentLoaded && !HasError && !IsLoading && Kind != "artist" && Songs.Count == 0;

    /// <summary>True when the header offers "download all" (album / playlist track lists).</summary>
    public bool CanDownloadAll => (Kind == "album" || Kind == "playlist") && Songs.Count > 0;

    public bool CanCopyLink => !string.IsNullOrWhiteSpace(_entityUrl);

    /// <summary>Segoe Fluent Icons: Heart (EB51) / HeartFill (EB52) — the SongRow convention.</summary>
    public string FavoriteGlyph => IsFavoriteEntity ? "\uEB52" : "\uEB51";

    public string FavoriteName =>
        IsFavoriteEntity ? Res.Get("RemoveFromFavorites") : Res.Get("AddToFavorites");

    public string PlayName => Res.Get("Play");

    public string ShuffleName => IsShuffleOn ? Res.Get("ShuffleOn") : Res.Get("ShuffleOff");

    public string DownloadName => Res.Get("Download");

    /// <summary>Cancels an in-flight load (page navigated away).</summary>
    public void CancelLoads()
    {
        _loadCts?.Cancel();
        _player.StateChanged -= OnPlayerStateChanged;
    }

    public async Task LoadAsync(DetailNavigationArgs args)
    {
        _lastArgs = args;
        Kind = args.Kind;
        IsChart = args.IsChart;
        OnPropertyChanged(nameof(IsArtist));
        OnPropertyChanged(nameof(IsChart));
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        CancellationToken ct = _loadCts.Token;

        _player.StateChanged -= OnPlayerStateChanged;
        _player.StateChanged += OnPlayerStateChanged;
        IsShuffleOn = _player.Shuffle;

        ResetState(args);

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

            // Favorite state can resolve from the args alone, before
            // the detail lands.
            if (!string.IsNullOrWhiteSpace(args.Id))
            {
                _resolvedId = args.Id;
                await RefreshFavoriteEntityAsync();
            }

            switch (args.Kind)
            {
                case "album":
                    await LoadAlbumAsync(args, ct);
                    break;
                case "playlist":
                    await LoadPlaylistAsync(args, ct);
                    break;
                case "artist":
                    await LoadArtistAsync(args, ct);
                    break;
                default:
                    ErrorMessage = Res.Get("DetailUnknownKind");
                    break;
            }

            IsContentLoaded = true;

            foreach (SongItemViewModel row in Songs)
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

    /// <summary>Clears every surface and paints the instant header from the args' preview fields.</summary>
    private void ResetState(DetailNavigationArgs args)
    {
        IsLoading = true;
        ErrorMessage = null;
        IsContentLoaded = false;
        Songs.Clear();
        RankedSongs.Clear();
        Singles.Clear();
        TopAlbums.Clear();
        SimilarArtists.Clear();
        DedicatedPlaylists.Clear();
        FeaturedPlaylists.Clear();
        MoreByAlbums.Clear();
        AlbumArtists.Clear();
        BioSections.Clear();
        AboutFacts.Clear();
        HasMoreSongs = false;
        IsVerifiedArtist = false;
        IsFavoriteEntity = false;
        LatestRelease = null;
        WikiUrl = null;
        Description = string.Empty;
        IsDescriptionExpanded = false;
        BylineText = string.Empty;
        ProvenanceText = string.Empty;
        MoreByHeaderText = string.Empty;
        DedicatedHeaderText = string.Empty;
        _topSongsMaster.Clear();
        _topSongsRevealed = 0;
        _artistSongsNextPage = 0;
        _topSongsEnded = true;
        _resolvedId = args.Id;
        _entityUrl = null;
        _favoriteSubtitle = args.Subtitle;
        _favoriteImageUrl = args.ImageUrl;

        EyebrowText = args.Kind switch
        {
            "album" => Res.Get("AlbumEyebrow"),
            "artist" => Res.Get("ArtistEyebrow"),
            "playlist" => Res.Get(args.IsChart ? "ChartEyebrow" : "PlaylistEyebrow"),
            _ => string.Empty,
        };

        // Instant header: what the tapped card already knew.
        Title = args.Title ?? string.Empty;
        MetaText = args.Subtitle ?? string.Empty;
        HeaderArtwork = ArtworkHelper.From(args.ImageUrl);
        OnPropertyChanged(nameof(HeaderArtwork));
        NotifyContentVisibility();
    }

    [RelayCommand]
    private Task RetryAsync() =>
        _lastArgs is null ? Task.CompletedTask : LoadAsync(_lastArgs);

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        IReadOnlyList<Song> queue = EntityQueue();
        if (queue.Count > 0)
        {
            await PlaySongAsync(queue[0], queue);
        }
    }

    /// <summary>
    /// The shuffle disc: turning shuffle on starts the entity queue
    /// shuffled (the engine's play order is rebuilt by the setter);
    /// turning it off just clears the mode.
    /// </summary>
    [RelayCommand]
    private async Task ToggleShuffleAsync()
    {
        if (IsShuffleOn)
        {
            _player.Shuffle = false;
            IsShuffleOn = false;
            return;
        }

        _player.Shuffle = true;
        IsShuffleOn = true;
        await PlayAllAsync();
    }

    [RelayCommand]
    private async Task ToggleFavoriteEntityAsync()
    {
        if (string.IsNullOrWhiteSpace(_resolvedId))
        {
            return;
        }

        bool target = !IsFavoriteEntity;
        try
        {
            var entity = new FavoriteEntity(
                Kind, _resolvedId, Title, _favoriteSubtitle ?? string.Empty, _favoriteImageUrl, _entityUrl);
            await _store.SetFavoriteEntityAsync(entity, target);
            IsFavoriteEntity = target;
        }
        catch (Exception)
        {
            // Favorites are local best-effort; keep the shown state.
        }
    }

    /// <summary>Downloads every track of the album/playlist, sequentially (FX3).</summary>
    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        foreach (SongItemViewModel row in Songs)
        {
            await _downloadService.DownloadAsync(row.Song);
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (string.IsNullOrWhiteSpace(_entityUrl))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_entityUrl);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Whole entity as "play next": PlayNextAsync inserts directly
    /// behind the current track, so iterate in reverse to land the
    /// entity in its own order.
    /// </summary>
    [RelayCommand]
    private void PlayEntityNext()
    {
        IReadOnlyList<Song> queue = EntityQueue();
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            _ = _playback.PlayNextAsync(queue[i]);
        }
    }

    [RelayCommand]
    private void EnqueueEntity()
    {
        foreach (Song song in EntityQueue())
        {
            _ = _playback.EnqueueAsync(song);
        }
    }

    [RelayCommand]
    private void ToggleDescription() => IsDescriptionExpanded = !IsDescriptionExpanded;

    /// <summary>
    /// Top songs "Show more": reveal the next chunk of already-known
    /// songs first; when the master list is exhausted, fetch the next
    /// upstream page and reveal from it (accumulation rule).
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreSongsAsync()
    {
        if (!IsArtist)
        {
            return;
        }

        if (_topSongsRevealed < _topSongsMaster.Count)
        {
            await RevealTopSongsAsync(TopSongsChunk);
            return;
        }

        if (_topSongsEnded || _currentArtistId is not string artistId)
        {
            return;
        }

        try
        {
            PagedResult<Song> page = await _client.GetArtistSongsAsync(artistId, _artistSongsNextPage);
            _artistSongsNextPage++;
            var known = new HashSet<string>(_topSongsMaster.Select(s => s.Id));
            int added = 0;
            foreach (Song song in page.Items)
            {
                if (known.Add(song.Id))
                {
                    _topSongsMaster.Add(song);
                    added++;
                }
            }

            if (page.Items.Count == 0 || added == 0 ||
                (page.Total > 0 && _topSongsMaster.Count >= page.Total))
            {
                _topSongsEnded = true;
            }

            await RevealTopSongsAsync(TopSongsChunk);
        }
        catch (Exception)
        {
            // Paging is best-effort: stop offering more rather than
            // surfacing an error over an otherwise loaded page.
            _topSongsEnded = true;
            UpdateHasMoreSongs();
        }
    }

    private string? _currentArtistId;

    private async Task LoadAlbumAsync(DetailNavigationArgs args, CancellationToken ct)
    {
        Album album = string.IsNullOrWhiteSpace(args.Id) && !string.IsNullOrWhiteSpace(args.Token)
            ? await _client.GetAlbumByLinkTokenAsync(args.Token!, ct)
            : await _client.GetAlbumAsync(args.Id, ct);
        _resolvedId = album.Id;
        _entityUrl = album.Url;
        Title = album.Name;
        HeaderArtwork = ArtworkHelper.From(album.Image.Large ?? album.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));

        // Byline: structured artist names, tappable to their pages.
        IEnumerable<ArtistRef> bylineArtists = album.Artists.Primary.Count > 0
            ? album.Artists.Primary.Concat(album.Artists.Featured)
            : album.Artists.All;
        var seen = new HashSet<string>();
        foreach (ArtistRef artist in bylineArtists)
        {
            if (!string.IsNullOrWhiteSpace(artist.Id) && seen.Add(artist.Id))
            {
                AlbumArtists.Add(new DetailArtistLink(artist.Id, artist.Name));
            }
        }

        string artistsLine = string.Join(", ", AlbumArtists.Select(a => a.Name));
        _favoriteSubtitle = artistsLine.Length > 0 ? artistsLine : album.Language;
        _favoriteImageUrl = album.Image.Large ?? album.Image.Medium;

        MetaText = JoinMeta(
            album.Year?.ToString(CultureInfo.InvariantCulture),
            TitleCase(album.Language),
            Res.Format("PlaylistSongCountFormat", album.SongCount),
            FormatRuntime(album.Songs));
        Description = album.Description ?? string.Empty;

        IReadOnlyList<Song> all = album.Songs;
        foreach (Song song in all)
        {
            Songs.Add(MakeRow(song, () => all));
        }

        // Footer: provenance (year · label · © line), then
        // "More by {artist}" — each under its own guard.
        BuildAlbumProvenance(album);
        NotifyContentVisibility();
        await RefreshFavoriteEntityAsync();
        await LoadMoreByAsync(album, ct);
    }

    private void BuildAlbumProvenance(Album album)
    {
        string? label = album.Songs.FirstOrDefault()?.Label;
        string? copyright = string.IsNullOrWhiteSpace(album.CopyrightText)
            ? null
            : album.CopyrightText.StartsWith("©", StringComparison.Ordinal)
                ? album.CopyrightText
                : "© " + album.CopyrightText;
        ProvenanceText = JoinMeta(
            album.Year?.ToString(CultureInfo.InvariantCulture),
            label,
            copyright);
        OnPropertyChanged(nameof(HasProvenance));
    }

    private async Task LoadMoreByAsync(Album album, CancellationToken ct)
    {
        ArtistRef? primary = album.Artists.Primary.FirstOrDefault();
        if (primary is null || string.IsNullOrWhiteSpace(primary.Id))
        {
            return;
        }

        try
        {
            PagedResult<Album> page = await _client.GetArtistAlbumsAsync(primary.Id, 0, cancellationToken: ct);
            foreach (Album other in page.Items)
            {
                if (!string.Equals(other.Id, album.Id, StringComparison.Ordinal))
                {
                    MoreByAlbums.Add(TileItemViewModel.FromAlbum(other));
                }
            }

            if (MoreByAlbums.Count > 0)
            {
                MoreByHeaderText = Res.Format("MoreByFormat", primary.Name);
            }

            OnPropertyChanged(nameof(HasMoreBy));
        }
        catch (Exception)
        {
            // The footer band is a bonus; its failure stays silent.
        }
    }

    private async Task LoadPlaylistAsync(DetailNavigationArgs args, CancellationToken ct)
    {
        Playlist playlist;
        if (string.IsNullOrWhiteSpace(args.Id) && !string.IsNullOrWhiteSpace(args.Token))
        {
            Playlist firstPage = await _client.GetPlaylistByLinkTokenAsync(args.Token!, cancellationToken: ct);
            try
            {
                playlist = await _client.GetPlaylistWithAllSongsAsync(firstPage.Id, cancellationToken: ct);
            }
            catch (Exception)
            {
                playlist = firstPage;
            }
        }
        else
        {
            playlist = await _client.GetPlaylistWithAllSongsAsync(args.Id, cancellationToken: ct);
        }

        _resolvedId = playlist.Id;
        _entityUrl = playlist.Url;
        Title = playlist.Name;
        HeaderArtwork = ArtworkHelper.From(playlist.Image.Large ?? playlist.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));
        BylineText = playlist.OwnerName ?? string.Empty;
        OnPropertyChanged(nameof(HasByline));
        _favoriteSubtitle = playlist.OwnerName;
        _favoriteImageUrl = playlist.Image.Large ?? playlist.Image.Medium;

        string? followers = DisplayFormatting.Count(playlist.FollowerCount);
        MetaText = JoinMeta(
            Res.Format("PlaylistSongCountFormat", playlist.SongCount),
            FormatRuntime(playlist.Songs),
            playlist.LastUpdatedUtc is { } updated
                ? Res.Format("DetailUpdatedFormat", updated.ToString("d MMM yyyy", CultureInfo.CurrentCulture))
                : null,
            followers.Length > 0 ? Res.Format("ArtistFollowersFormat", followers) : null);
        Description = playlist.Description ?? string.Empty;

        IReadOnlyList<Song> all = playlist.Songs;
        foreach (Song song in all)
        {
            Songs.Add(MakeRow(song, () => all));
        }

        if (IsChart)
        {
            int rank = 1;
            foreach (SongItemViewModel row in Songs)
            {
                RankedSongs.Add(new RankedSongRow(rank++, row));
            }
        }

        NotifyContentVisibility();
        await RefreshFavoriteEntityAsync();
    }

    private async Task LoadArtistAsync(DetailNavigationArgs args, CancellationToken ct)
    {
        string artistId = args.Id;
        _currentArtistId = artistId;
        Artist artist = await _client.GetArtistAsync(artistId, cancellationToken: ct);
        _resolvedId = artist.Id;
        _entityUrl = artist.Url;
        Title = artist.Name;
        HeaderArtwork = ArtworkHelper.From(artist.Image.Large ?? artist.Image.Medium);
        OnPropertyChanged(nameof(HeaderArtwork));
        IsVerifiedArtist = artist.IsVerified;
        _favoriteSubtitle = artist.Subtitle;
        _favoriteImageUrl = artist.Image.Large ?? artist.Image.Medium;

        // Meta: the "Listeners" number (fan_count, upstream's own
        // convention); fall back to the ready-made subtitle byline,
        // then to the plain follower count.
        string listeners = DisplayFormatting.Count(artist.FanCount);
        MetaText = listeners.Length > 0
            ? Res.Format("ArtistListenersFormat", listeners)
            : !string.IsNullOrWhiteSpace(artist.Subtitle)
                ? artist.Subtitle!
                : JoinMeta(
                    DisplayFormatting.Count(artist.FollowerCount) is { Length: > 0 } f
                        ? Res.Format("ArtistFollowersFormat", f)
                        : null,
                    TitleCase(artist.DominantLanguage));

        // Top songs: the payload embeds the first page (10); reveal
        // five at a time, then page (see LoadMoreSongsAsync). An
        // embedded page that came back short means the list is
        // complete — no further pages exist.
        _topSongsMaster.AddRange(artist.TopSongs);
        _topSongsEnded = artist.TopSongs.Count < 10;
        _artistSongsNextPage = 1;
        await RevealTopSongsAsync(TopSongsChunk);

        FillArtistSections(artist);
        NotifyContentVisibility();
        await RefreshFavoriteEntityAsync();

        // Albums depth: the payload embeds the first 10; fetch
        // further pages under their own guard and append new ones.
        await LoadMoreArtistAlbumsAsync(artistId, ct);
    }

    /// <summary>Band/About sections from the artist payload — each fill guarded independently.</summary>
    private void FillArtistSections(Artist artist)
    {
        try
        {
            if (artist.LatestRelease is { } latest)
            {
                LatestRelease = TileItemViewModel.FromAlbum(latest);
                OnPropertyChanged(nameof(HasLatestRelease));
            }
        }
        catch (Exception)
        {
            // Section-local failure; the page stays up.
        }

        try
        {
            foreach (Album album in artist.TopAlbums)
            {
                TopAlbums.Add(TileItemViewModel.FromAlbum(album));
            }
        }
        catch (Exception)
        {
        }

        try
        {
            // Singles are album-lite items (contract): album cards.
            foreach (Album single in artist.Singles)
            {
                Singles.Add(TileItemViewModel.FromAlbum(single));
            }
        }
        catch (Exception)
        {
        }

        try
        {
            foreach (Playlist playlist in artist.DedicatedPlaylists)
            {
                DedicatedPlaylists.Add(TileItemViewModel.FromPlaylist(playlist));
            }

            if (DedicatedPlaylists.Count > 0)
            {
                DedicatedHeaderText = Res.Format("JustArtistFormat", artist.Name);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            foreach (Playlist playlist in artist.FeaturedPlaylists)
            {
                FeaturedPlaylists.Add(TileItemViewModel.FromPlaylist(playlist));
            }
        }
        catch (Exception)
        {
        }

        try
        {
            foreach (BioEntry entry in artist.Bio
                         .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                         .OrderBy(b => b.Sequence))
            {
                BioSections.Add(new DetailBioSection(
                    string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title!.Trim(),
                    entry.Text!.Trim()));
            }

            string followers = DisplayFormatting.Count(artist.FollowerCount);
            if (followers.Length > 0)
            {
                AboutFacts.Add(Res.Format("ArtistFollowersFormat", followers));
            }

            if (!string.IsNullOrWhiteSpace(artist.DateOfBirth))
            {
                AboutFacts.Add(Res.Format("BornFormat", FormatDob(artist.DateOfBirth!)));
            }

            foreach (string language in artist.AvailableLanguages
                         .Where(l => !string.IsNullOrWhiteSpace(l) &&
                                     !string.Equals(l, "unknown", StringComparison.OrdinalIgnoreCase)))
            {
                AboutFacts.Add(TitleCase(language) ?? language);
            }

            WikiUrl = !string.IsNullOrWhiteSpace(artist.Wiki) &&
                      Uri.TryCreate(artist.Wiki, UriKind.Absolute, out Uri? wiki)
                ? wiki
                : null;
        }
        catch (Exception)
        {
        }

        try
        {
            foreach (ArtistRef similar in artist.SimilarArtists)
            {
                SimilarArtists.Add(TileItemViewModel.FromArtistRef(similar));
            }
        }
        catch (Exception)
        {
        }

        OnPropertyChanged(nameof(HasAbout));
        OnPropertyChanged(nameof(HasWiki));
    }

    private async Task LoadMoreArtistAlbumsAsync(string artistId, CancellationToken ct)
    {
        try
        {
            var known = new HashSet<string>(TopAlbums.Select(t => t.Id));
            int page = 1;
            int total = int.MaxValue;
            while (TopAlbums.Count < total && page <= 4 && !ct.IsCancellationRequested)
            {
                PagedResult<Album> result = await _client.GetArtistAlbumsAsync(artistId, page, cancellationToken: ct);
                total = result.Total;
                if (result.Items.Count == 0)
                {
                    break;
                }

                foreach (Album album in result.Items)
                {
                    if (known.Add(album.Id))
                    {
                        TopAlbums.Add(TileItemViewModel.FromAlbum(album));
                    }
                }

                page++;
            }

            OnPropertyChanged(nameof(HasTopAlbums));
        }
        catch (Exception)
        {
            // Depth is a bonus; the embedded albums already render.
        }
    }

    private async Task RevealTopSongsAsync(int count)
    {
        int revealedBefore = Songs.Count;
        int target = Math.Min(_topSongsRevealed + count, _topSongsMaster.Count);
        while (_topSongsRevealed < target)
        {
            Songs.Add(MakeRow(_topSongsMaster[_topSongsRevealed], () => _topSongsMaster));
            _topSongsRevealed++;
        }

        for (int i = revealedBefore; i < Songs.Count; i++)
        {
            await Songs[i].RefreshFavoriteAsync();
        }

        UpdateHasMoreSongs();
        OnPropertyChanged(nameof(HasSongs));
    }

    private void UpdateHasMoreSongs() =>
        HasMoreSongs = _topSongsRevealed < _topSongsMaster.Count || !_topSongsEnded;

    private void OnPlayerStateChanged(object? sender, EventArgs e) =>
        IsShuffleOn = _player.Shuffle;

    private async Task RefreshFavoriteEntityAsync()
    {
        if (string.IsNullOrWhiteSpace(_resolvedId))
        {
            return;
        }

        try
        {
            IsFavoriteEntity = await _store.IsFavoriteEntityAsync(Kind, _resolvedId);
        }
        catch (Exception)
        {
            // Store not ready — the heart renders unfavourited.
        }
    }

    /// <summary>The queue an entity-level verb acts on: all loaded songs of the entity.</summary>
    private IReadOnlyList<Song> EntityQueue() =>
        IsArtist ? _topSongsMaster : Songs.Select(r => r.Song).ToList();

    private SongItemViewModel MakeRow(Song song, Func<IReadOnlyList<Song>> queueProvider) =>
        new(song, _store, s => PlaySongAsync(s, queueProvider()), s => AddToPlaylistRequested?.Invoke(s),
            playNextHandler: s => { _ = _playback.PlayNextAsync(s); },
            addToQueueHandler: s => { _ = _playback.EnqueueAsync(s); },
            downloadHandler: s => _downloadService.DownloadAsync(s));

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

    /// <summary>Total runtime of the loaded songs ("41 min" / "1 hr 12 min"); empty when unknown.</summary>
    private static string FormatRuntime(IEnumerable<Song> songs)
    {
        long totalSeconds = songs.Sum(s => (long)(s.DurationSeconds ?? 0));
        if (totalSeconds <= 0)
        {
            return string.Empty;
        }

        long hours = totalSeconds / 3600;
        long minutes = (totalSeconds % 3600) / 60;
        return hours > 0
            ? Res.Format("DetailRuntimeHoursFormat", hours, minutes)
            : Res.Format("DetailRuntimeMinutesFormat", Math.Max(1, minutes));
    }

    /// <summary>Upstream languages arrive lowercase ("hindi"); display them titled.</summary>
    private static string? TitleCase(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());

    /// <summary>Upstream DOB is "dd-MM-yyyy"; render it in the current culture's long form when parseable.</summary>
    private static string FormatDob(string dob) =>
        DateTime.TryParseExact(dob.Trim(), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            : dob;

    private void NotifyContentVisibility()
    {
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasSingles));
        OnPropertyChanged(nameof(HasTopAlbums));
        OnPropertyChanged(nameof(HasSimilarArtists));
        OnPropertyChanged(nameof(HasDedicatedPlaylists));
        OnPropertyChanged(nameof(HasFeaturedPlaylists));
        OnPropertyChanged(nameof(HasMoreBy));
        OnPropertyChanged(nameof(HasLatestRelease));
        OnPropertyChanged(nameof(HasAlbumArtists));
        OnPropertyChanged(nameof(HasByline));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(ShowDescriptionToggle));
        OnPropertyChanged(nameof(HasAbout));
        OnPropertyChanged(nameof(HasProvenance));
        OnPropertyChanged(nameof(CanDownloadAll));
        OnPropertyChanged(nameof(CanCopyLink));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(ShowDescriptionToggle));
    }

    partial void OnIsDescriptionExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(DescriptionMaxLines));
        OnPropertyChanged(nameof(DescriptionToggleText));
    }

    partial void OnIsFavoriteEntityChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteName));
    }

    partial void OnIsShuffleOnChanged(bool value) => OnPropertyChanged(nameof(ShuffleName));

    partial void OnIsContentLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnBylineTextChanged(string value) => OnPropertyChanged(nameof(HasByline));
}
