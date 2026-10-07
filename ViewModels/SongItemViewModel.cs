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
///
/// The [Bindable] attribute is required by the shared song-row
/// template (Templates/SongRowTemplates.xaml): it binds with classic
/// {Binding} + x:DataType (D1 — compiled bindings resolved against a
/// null source at runtime in that dictionary), and [Bindable] is
/// what keeps those bindings AOT/trim-clean (no WMC1510).
/// </summary>
[Microsoft.UI.Xaml.Data.Bindable]
public partial class SongItemViewModel : ObservableObject
{
    private readonly ILibraryStore _store;
    private readonly Func<Song, Task> _playHandler;
    private readonly Action<Song>? _addToPlaylistHandler;
    private readonly Func<SongItemViewModel, Task>? _removeHandler;
    private readonly Action<SongItemViewModel>? _favoriteChangedHandler;
    private readonly Action<Song>? _playNextHandler;
    private readonly Action<Song>? _addToQueueHandler;
    private readonly Func<Song, Task>? _downloadHandler;
    public SongItemViewModel(
        Song song,
        ILibraryStore store,
        Func<Song, Task> playHandler,
        Action<Song>? addToPlaylistHandler = null,
        Func<SongItemViewModel, Task>? removeHandler = null,
        Action<SongItemViewModel>? favoriteChangedHandler = null,
        Action<Song>? playNextHandler = null,
        Action<Song>? addToQueueHandler = null,
        Func<Song, Task>? downloadHandler = null)
    {
        Song = song;
        _store = store;
        _playHandler = playHandler;
        _addToPlaylistHandler = addToPlaylistHandler;
        _removeHandler = removeHandler;
        _favoriteChangedHandler = favoriteChangedHandler;
        _playNextHandler = playNextHandler;
        _addToQueueHandler = addToQueueHandler;
        _downloadHandler = downloadHandler;
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

    // ----------------------------------------------------------------
    // Download (FX3): present only when the owning page supplies a
    // download handler (Library / Detail) — the row menu hides the
    // item where CanDownload is false, mirroring CanAddToPlaylist.
    // ----------------------------------------------------------------

    public bool CanDownload => _downloadHandler is not null;

    public string DownloadMenuText => Res.Get("Download");

    [RelayCommand]
    private Task DownloadAsync() =>
        _downloadHandler is null ? Task.CompletedTask : _downloadHandler(Song);
}
