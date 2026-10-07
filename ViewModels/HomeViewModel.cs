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
/// Home (design §9.2): hero from the top trending entity, then browse-
/// module sections — Jump back in (local history), Trending songs, New
/// albums, Charts, Top playlists, Browse discover. Browse entities are
/// classified by SHAPE (<see cref="HomeEntityClassifier"/>), never by
/// the unreliable upstream entity type. Radio/shows sections are
/// display-only in v1 and not rendered.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly JioSaavnClient _client;
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;
    private CancellationTokenSource? _loadCts;

    public HomeViewModel(JioSaavnClient client, IPlaybackGateway playback, ILibraryStore store)
    {
        _client = client;
        _playback = playback;
        _store = store;
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    public ObservableCollection<SongItemViewModel> JumpBackInItems { get; } = new();

    public ObservableCollection<HomeItemViewModel> TrendingItems { get; } = new();

    public ObservableCollection<HomeItemViewModel> NewAlbumItems { get; } = new();

    public ObservableCollection<HomeItemViewModel> ChartItems { get; } = new();

    public ObservableCollection<HomeItemViewModel> TopPlaylistItems { get; } = new();

    public ObservableCollection<HomeItemViewModel> DiscoverItems { get; } = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial HomeItemViewModel? Hero { get; set; }

    public bool HasHero => Hero is not null;

    public bool HasJumpBackIn => JumpBackInItems.Count > 0;

    public bool HasTrending => TrendingItems.Count > 0;

    public bool HasNewAlbums => NewAlbumItems.Count > 0;

    public bool HasCharts => ChartItems.Count > 0;

    public bool HasTopPlaylists => TopPlaylistItems.Count > 0;

    public bool HasDiscover => DiscoverItems.Count > 0;

    /// <summary>Time-aware greeting (design §9.2 hero block).</summary>
    public string Greeting
    {
        get
        {
            int hour = DateTime.Now.Hour;
            return hour switch
            {
                < 12 => Res.Get("GoodMorning"),
                < 17 => Res.Get("GoodAfternoon"),
                _ => Res.Get("GoodEvening"),
            };
        }
    }

    /// <summary>Cancels an in-flight load (page navigated away).</summary>
    public void CancelLoads() => _loadCts?.Cancel();

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        CancellationToken ct = _loadCts.Token;

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            try
            {
                await _store.InitializeAsync();
            }
            catch (Exception)
            {
                // Local data degrades (no history/favourites); the
                // upstream sections below must still load.
            }

            HomeModules modules = await _client.GetBrowseModulesAsync(ct);

            FillEntities(TrendingItems, modules.NewTrending);
            FillEntities(NewAlbumItems, modules.NewAlbums);
            FillEntities(ChartItems, modules.Charts);
            FillEntities(TopPlaylistItems, modules.TopPlaylists);
            FillEntities(DiscoverItems, modules.BrowseDiscover);

            Hero = TrendingItems.FirstOrDefault() ?? NewAlbumItems.FirstOrDefault();
            NotifySectionVisibility();

            // Local section — a store failure must not take the
            // (already loaded) remote sections down with it.
            try
            {
                IReadOnlyList<Song> history = await _store.GetHistoryAsync(20);
                JumpBackInItems.Clear();
                foreach (Song song in history)
                {
                    SongItemViewModel row = MakeRow(song, () => HistorySongs());
                    JumpBackInItems.Add(row);
                    await row.RefreshFavoriteAsync();
                }

                NotifySectionVisibility();
            }
            catch (Exception)
            {
                // History unavailable — section stays hidden.
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnPropertyChanged(nameof(HasError));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Hero primary command: play the trending set — track-carrying
    /// items contribute their tracks, song-shaped items are batch-
    /// resolved in ONE song.getDetails call, then shuffled (§9.2
    /// "Play (shuffle-all trending)").
    /// </summary>
    [RelayCommand]
    private async Task PlayHeroAsync()
    {
        if (Hero is null)
        {
            return;
        }

        if (Hero.Kind == HomeEntityKind.Album && Hero.Entity.Tracks.Count > 0)
        {
            await PlaySongAsync(Hero.Entity.Tracks[0], Hero.Entity.Tracks);
            return;
        }

        var ids = new List<string>();
        var queue = new List<Song>();
        foreach (HomeItemViewModel item in TrendingItems)
        {
            if (item.Kind == HomeEntityKind.Song)
            {
                ids.Add(item.Entity.Id);
            }
            else if (item.Kind == HomeEntityKind.Album)
            {
                queue.AddRange(item.Entity.Tracks);
            }
        }

        if (ids.Count > 0)
        {
            IReadOnlyList<Song> resolved = await _client.GetSongsByIdsAsync(ids);
            queue.AddRange(resolved);
        }

        // Fisher–Yates shuffle for the hero "shuffle-all" command.
        for (int i = queue.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (queue[i], queue[j]) = (queue[j], queue[i]);
        }

        if (queue.Count > 0)
        {
            await PlaySongAsync(queue[0], queue);
        }
    }

    /// <summary>Plays a browse entity: songs resolve first; albums play their embedded tracks.</summary>
    public async Task PlayEntityAsync(HomeItemViewModel item)
    {
        if (item.Kind == HomeEntityKind.Album && item.Entity.Tracks.Count > 0)
        {
            await PlaySongAsync(item.Entity.Tracks[0], item.Entity.Tracks);
            return;
        }

        if (item.Kind == HomeEntityKind.Song)
        {
            IReadOnlyList<Song> resolved = await _client.GetSongsByIdsAsync(new[] { item.Entity.Id });
            if (resolved.Count > 0)
            {
                await PlaySongAsync(resolved[0], resolved);
            }
        }
    }

    private IReadOnlyList<Song> HistorySongs() =>
        JumpBackInItems.Select(r => r.Song).ToList();

    private void FillEntities(ObservableCollection<HomeItemViewModel> target, IReadOnlyList<HomeEntity> entities)
    {
        target.Clear();
        foreach (HomeEntity entity in entities)
        {
            target.Add(new HomeItemViewModel(entity, item => PlayEntityAsync(item)));
        }
    }

    private SongItemViewModel MakeRow(Song song, Func<IReadOnlyList<Song>> queueProvider) =>
        new(song, _store, s => PlaySongAsync(s, queueProvider()), s => AddToPlaylistRequested?.Invoke(s));

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

    private void NotifySectionVisibility()
    {
        OnPropertyChanged(nameof(HasHero));
        OnPropertyChanged(nameof(HasJumpBackIn));
        OnPropertyChanged(nameof(HasTrending));
        OnPropertyChanged(nameof(HasNewAlbums));
        OnPropertyChanged(nameof(HasCharts));
        OnPropertyChanged(nameof(HasTopPlaylists));
        OnPropertyChanged(nameof(HasDiscover));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnHeroChanged(HomeItemViewModel? value) => OnPropertyChanged(nameof(HasHero));
}
