using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Playback;

namespace Omega.ViewModels;

/// <summary>
/// Library (design §9.4): all-local data behind <see cref="ILibraryStore"/>
/// — Favourites / Playlists / History / Downloads. Works fully offline.
/// Playlist create/rename validation lives in the page's dialogs
/// (non-empty name); these methods assume a validated name.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;

    public LibraryViewModel(IPlaybackGateway playback, ILibraryStore store)
    {
        _playback = playback;
        _store = store;
    }

    /// <summary>Raised when a song's "Add to playlist" action is chosen; the page shows the picker.</summary>
    public event Action<Song>? AddToPlaylistRequested;

    public ObservableCollection<SongItemViewModel> Favorites { get; } = new();

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    public ObservableCollection<SongItemViewModel> PlaylistSongs { get; } = new();

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

    public bool HasHistory => History.Count > 0;

    public bool HasDownloads => Downloads.Count > 0;

    public string FavoritesCountText => Res.Format("FavoritesCountFormat", Favorites.Count);

    public string PlaylistsCountText => Res.Format("PlaylistsCountFormat", Playlists.Count);

    public string HistoryCountText => Res.Format("HistoryCountFormat", History.Count);

    public string DownloadsCountText => Res.Format("DownloadsCountFormat", Downloads.Count);

    public async Task LoadAllAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await _store.InitializeAsync();
            await LoadFavoritesAsync();
            await LoadPlaylistsAsync();
            await LoadHistoryAsync();
            await LoadDownloadsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyCounts();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAllAsync();

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
        await _store.DeleteDownloadAsync(item.Record.SongId);
        Downloads.Remove(item);
        NotifyCounts();
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
            favoriteChanged);

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
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(FavoritesCountText));
        OnPropertyChanged(nameof(PlaylistsCountText));
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
