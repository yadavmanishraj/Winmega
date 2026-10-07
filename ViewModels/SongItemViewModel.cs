using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Core.Persistence;

namespace Omega.ViewModels;

/// <summary>
/// One playable song row, shared by Home (history rail), Search,
/// Detail and Library. Playback/favourite/playlist actions are
/// delegated to the owning page ViewModel through callbacks so the
/// queue context always comes from the page's current list; the
/// favourite toggle talks to <see cref="ILibraryStore"/> directly
/// (it is page-independent).
///
/// AOT rule (MVVMTK0045): [ObservableProperty] on PARTIAL PROPERTIES
/// only — never the field form.
/// </summary>
public partial class SongItemViewModel : ObservableObject
{
    private readonly ILibraryStore _store;
    private readonly Func<Song, Task> _playHandler;
    private readonly Action<Song>? _addToPlaylistHandler;
    private readonly Func<SongItemViewModel, Task>? _removeHandler;
    private readonly Action<SongItemViewModel>? _favoriteChangedHandler;
    private readonly Action<Song>? _playNextHandler;
    private readonly Action<Song>? _addToQueueHandler;

    // NOTE for the downloads workstream (FX3): append any download
    // handler parameter AFTER addToQueueHandler so existing
    // positional call sites keep compiling, mirroring the pattern
    // below (CanDownload flag + a Download RelayCommand).
    public SongItemViewModel(
        Song song,
        ILibraryStore store,
        Func<Song, Task> playHandler,
        Action<Song>? addToPlaylistHandler = null,
        Func<SongItemViewModel, Task>? removeHandler = null,
        Action<SongItemViewModel>? favoriteChangedHandler = null,
        Action<Song>? playNextHandler = null,
        Action<Song>? addToQueueHandler = null)
    {
        Song = song;
        _store = store;
        _playHandler = playHandler;
        _addToPlaylistHandler = addToPlaylistHandler;
        _removeHandler = removeHandler;
        _favoriteChangedHandler = favoriteChangedHandler;
        _playNextHandler = playNextHandler;
        _addToQueueHandler = addToQueueHandler;
    }

    public Song Song { get; }

    public string Title => Song.Name;

    public string Artists => Song.PrimaryArtistNames;

    public string DurationText => DisplayFormatting.Duration(Song.DurationSeconds);

    public ImageSource? Artwork => ArtworkHelper.From(Song.Image.Medium ?? Song.Image.Small);

    public bool CanAddToPlaylist => _addToPlaylistHandler is not null;

    public bool CanPlayNext => _playNextHandler is not null;

    public bool CanAddToQueue => _addToQueueHandler is not null;

    public bool CanRemove => _removeHandler is not null;

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>Segoe Fluent Icons: Heart (EB51) / HeartFill (EB52).</summary>
    public string FavoriteGlyph => IsFavorite ? "\uEB52" : "\uEB51";

    public string FavoriteMenuText =>
        IsFavorite ? Res.Get("RemoveFromFavorites") : Res.Get("AddToFavorites");

    public string RemoveMenuText => Res.Get("RemoveFromPlaylist");

    /// <summary>Automation name for the row's overflow ("…") button.</summary>
    public string MoreActionsName => Res.Get("MoreActions");

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteMenuText));
    }

    /// <summary>Loads the persisted favourite state (local store — cheap, per row).</summary>
    public async Task RefreshFavoriteAsync()
    {
        try
        {
            IsFavorite = await _store.IsFavoriteAsync(Song.Id);
        }
        catch (Exception)
        {
            // Store not ready yet — the row simply renders unfavourited.
        }
    }

    [RelayCommand]
    private Task PlayAsync() => _playHandler(Song);

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        bool target = !IsFavorite;
        try
        {
            await _store.SetFavoriteAsync(Song, target);
            IsFavorite = target;
            _favoriteChangedHandler?.Invoke(this);
        }
        catch (Exception)
        {
            // Leave the rendered state unchanged on store failure.
        }
    }

    [RelayCommand]
    private void AddToPlaylist() => _addToPlaylistHandler?.Invoke(Song);

    [RelayCommand]
    private void PlayNext() => _playNextHandler?.Invoke(Song);

    [RelayCommand]
    private void AddToQueue() => _addToQueueHandler?.Invoke(Song);

    [RelayCommand]
    private Task RemoveAsync() =>
        _removeHandler is null ? Task.CompletedTask : _removeHandler(this);
}
