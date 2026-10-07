using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Playback;
using Omega.Core.Upstream;

namespace Omega.ViewModels;

/// <summary>
/// Search (design §9.3): AutoSuggestBox suggestions come from the
/// global search's top-query titles (debounced in the page); submit
/// runs all five result kinds into their own tabs — Top results
/// (lightweight, resolved on play), Songs, Albums, Artists,
/// Playlists. Typed results page by the accumulation rule
/// (UPSTREAM_VALIDATION §1): stop when accumulated ≥ total or a page
/// comes back short/empty — never trust the upstream <c>start</c>.
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private const int PageSize = 20;

    private readonly JioSaavnClient _client;
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;
    private CancellationTokenSource? _searchCts;

    private readonly PagedTab<Song, SongItemViewModel> _songsTab;
    private readonly PagedTab<Album, TileItemViewModel> _albumsTab;
    private readonly PagedTab<ArtistRef, TileItemViewModel> _artistsTab;
    private readonly PagedTab<Playlist, TileItemViewModel> _playlistsTab;

    public SearchViewModel(JioSaavnClient client, IPlaybackGateway playback, ILibraryStore store)
    {
        _client = client;
        _playback = playback;
        _store = store;

        _songsTab = new PagedTab<Song, SongItemViewModel>(
            page => _client.SearchSongsAsync(Query, page, PageSize, Token()),
            song => MakeRow(song),
            Songs);
        _albumsTab = new PagedTab<Album, TileItemViewModel>(
            page => _client.SearchAlbumsAsync(Query, page, PageSize, Token()),
            album => TileItemViewModel.FromAlbum(album),
            Albums);
        _artistsTab = new PagedTab<ArtistRef, TileItemViewModel>(
            page => _client.SearchArtistsAsync(Query, page, PageSize, Token()),
            artist => TileItemViewModel.FromArtistRef(artist),
            Artists);
        _playlistsTab = new PagedTab<Playlist, TileItemViewModel>(
            page => _client.SearchPlaylistsAsync(Query, page, PageSize, Token()),
            playlist => TileItemViewModel.FromPlaylist(playlist),
            Playlists);
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    public ObservableCollection<string> Suggestions { get; } = new();

    public ObservableCollection<SearchResultItemViewModel> TopResults { get; } = new();

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    public ObservableCollection<TileItemViewModel> Albums { get; } = new();

    public ObservableCollection<TileItemViewModel> Artists { get; } = new();

    public ObservableCollection<TileItemViewModel> Playlists { get; } = new();

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>True once a search has completed (drives idle vs no-results states).</summary>
    [ObservableProperty]
    public partial bool HasSearched { get; set; }

    public bool ShowIdle => !HasSearched && !IsLoading;

    public bool ShowNoResults =>
        HasSearched && !IsLoading && ErrorMessage is null &&
        TopResults.Count == 0 && Songs.Count == 0 && Albums.Count == 0 &&
        Artists.Count == 0 && Playlists.Count == 0;

    public string NoResultsText => Res.Format("SearchNoResultsFormat", Query);

    [ObservableProperty]
    public partial bool HasMoreSongs { get; set; }

    [ObservableProperty]
    public partial bool HasMoreAlbums { get; set; }

    [ObservableProperty]
    public partial bool HasMoreArtists { get; set; }

    [ObservableProperty]
    public partial bool HasMorePlaylists { get; set; }

    /// <summary>Cancels an in-flight search (page navigated away).</summary>
    public void CancelLoads() => _searchCts?.Cancel();

    private CancellationToken Token() => _searchCts?.Token ?? default;

    /// <summary>Debounced suggestion fetch for the AutoSuggestBox (page owns the debounce).</summary>
    public async Task LoadSuggestionsAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Suggestions.Clear();
            return;
        }

        try
        {
            SearchResults results = await _client.SearchAllAsync(text, Token());
            Suggestions.Clear();
            foreach (SearchItem item in results.TopQuery.Take(8))
            {
                if (!string.IsNullOrWhiteSpace(item.Title))
                {
                    Suggestions.Add(item.Title);
                }
            }
        }
        catch (Exception)
        {
            // Suggestions are best-effort; submit still runs the full search.
        }
    }

    public async Task SearchAsync(string query)
    {
        Query = query?.Trim() ?? string.Empty;
        if (Query.Length == 0)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        IsLoading = true;
        ErrorMessage = null;
        NotifyStateVisibility();
        int failures = 0;
        string? firstError = null;

        try
        {
            await _store.InitializeAsync();
        }
        catch (Exception)
        {
            // Favourite toggles degrade; search itself is upstream-only.
        }

        // Top results (lightweight global search).
        try
        {
            SearchResults all = await _client.SearchAllAsync(Query, Token());
            TopResults.Clear();
            foreach (SearchItem item in all.Songs
                         .Concat(all.Albums).Concat(all.Artists).Concat(all.Playlists).Take(30))
            {
                TopResults.Add(new SearchResultItemViewModel(item, _client, _playback, _store));
            }

            foreach (SearchItem item in all.TopQuery.Take(5))
            {
                TopResults.Insert(0, new SearchResultItemViewModel(item, _client, _playback, _store));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failures++;
            firstError ??= ex.Message;
        }

        failures += await LoadTabAsync(_songsTab, value => HasMoreSongs = value);
        failures += await LoadTabAsync(_albumsTab, value => HasMoreAlbums = value);
        failures += await LoadTabAsync(_artistsTab, value => HasMoreArtists = value);
        failures += await LoadTabAsync(_playlistsTab, value => HasMorePlaylists = value);
        if (failures > 0 && firstError is null)
        {
            firstError = "Some result types failed to load.";
        }

        foreach (SongItemViewModel row in Songs)
        {
            await row.RefreshFavoriteAsync();
        }

        HasSearched = true;
        IsLoading = false;
        if (failures == 5)
        {
            // Everything failed — a real error state with Retry.
            ErrorMessage = firstError;
        }

        NotifyStateVisibility();
    }

    [RelayCommand]
    private Task RetrySearchAsync() => SearchAsync(Query);

    [RelayCommand]
    private async Task LoadMoreSongsAsync()
    {
        await _songsTab.LoadMoreAsync();
        HasMoreSongs = _songsTab.HasMore;
        foreach (SongItemViewModel row in Songs)
        {
            await row.RefreshFavoriteAsync();
        }
    }

    [RelayCommand]
    private async Task LoadMoreAlbumsAsync()
    {
        await _albumsTab.LoadMoreAsync();
        HasMoreAlbums = _albumsTab.HasMore;
    }

    [RelayCommand]
    private async Task LoadMoreArtistsAsync()
    {
        await _artistsTab.LoadMoreAsync();
        HasMoreArtists = _artistsTab.HasMore;
    }

    [RelayCommand]
    private async Task LoadMorePlaylistsAsync()
    {
        await _playlistsTab.LoadMoreAsync();
        HasMorePlaylists = _playlistsTab.HasMore;
    }

    private async Task<int> LoadTabAsync<TItem, TRow>(
        PagedTab<TItem, TRow> tab, Action<bool> setHasMore)
    {
        try
        {
            await tab.LoadFirstAsync();
            setHasMore(tab.HasMore);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception)
        {
            setHasMore(false);
            return 1;
        }
    }

    private SongItemViewModel MakeRow(Song song) =>
        new(song, _store, PlaySongAsync, s => AddToPlaylistRequested?.Invoke(s));

    private async Task PlaySongAsync(Song song)
    {
        IReadOnlyList<Song> queue = Songs.Select(r => r.Song).ToList();
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

    private void NotifyStateVisibility()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(NoResultsText));
    }

    partial void OnErrorMessageChanged(string? value) => NotifyStateVisibility();

    partial void OnHasSearchedChanged(bool value) => NotifyStateVisibility();

    partial void OnIsLoadingChanged(bool value) => NotifyStateVisibility();

    /// <summary>
    /// One typed result tab with accumulation-rule paging: the next
    /// page index advances only over pages actually fetched, and an
    /// empty page terminates the tab regardless of <c>total</c>.
    /// </summary>
    private sealed class PagedTab<TItem, TRow>
    {
        private readonly Func<int, Task<PagedResult<TItem>>> _fetch;
        private readonly Func<TItem, TRow> _map;
        private int _nextPage;

        public PagedTab(
            Func<int, Task<PagedResult<TItem>>> fetch,
            Func<TItem, TRow> map,
            ObservableCollection<TRow> target)
        {
            _fetch = fetch;
            _map = map;
            Target = target;
        }

        public ObservableCollection<TRow> Target { get; }

        public int Total { get; private set; }

        public bool HasMore => Target.Count < Total;

        public async Task LoadFirstAsync()
        {
            Target.Clear();
            _nextPage = 0;
            Total = 0;
            await LoadMoreAsync();
        }

        public async Task LoadMoreAsync()
        {
            PagedResult<TItem> page = await _fetch(_nextPage);
            Total = page.Total;
            foreach (TItem item in page.Items)
            {
                Target.Add(_map(item));
            }

            _nextPage++;
            if (page.Items.Count == 0)
            {
                Total = Target.Count;
            }
        }
    }
}
