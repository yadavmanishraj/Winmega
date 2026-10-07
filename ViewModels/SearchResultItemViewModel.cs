using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Playback;
using Omega.Core.Upstream;

namespace Omega.ViewModels;

/// <summary>
/// One item of the Search "Top results" tab (global/autocomplete
/// search). These items are LIGHTWEIGHT (design §5.2): a song carries
/// no stream URLs, so Play first resolves it through
/// <c>song.getDetails</c>. Kind classification uses the item's
/// upstream <c>type</c> — typed search results carry a reliable type
/// (unlike browse-module sections, where shape rules apply);
/// <c>song_ids</c> presence is the shape fallback for songs.
/// </summary>
public sealed class SearchResultItemViewModel
{
    private readonly JioSaavnClient _client;
    private readonly IPlaybackGateway _playback;
    private readonly ILibraryStore _store;

    public SearchResultItemViewModel(
        SearchItem item, JioSaavnClient client, IPlaybackGateway playback, ILibraryStore store)
    {
        Item = item;
        _client = client;
        _playback = playback;
        _store = store;
        Kind = Classify(item);
        PlayCommand = new AsyncRelayCommand(PlayAsync);
    }

    public SearchItem Item { get; }

    public HomeEntityKind Kind { get; }

    public string Title => Item.Title;

    public string Subtitle =>
        Item.Description ?? Item.PrimaryArtistsText ?? Item.AlbumName ?? string.Empty;

    public ImageSource? Artwork => ArtworkHelper.From(Item.Image.Medium ?? Item.Image.Small);

    public bool IsSong => Kind == HomeEntityKind.Song;

    public bool IsNavigable =>
        Kind is HomeEntityKind.Album or HomeEntityKind.Playlist or HomeEntityKind.Artist;

    public DetailNavigationArgs? DetailArgs => Kind switch
    {
        HomeEntityKind.Album => DetailNavigationArgs.Album(Item.Id),
        HomeEntityKind.Playlist => DetailNavigationArgs.Playlist(Item.Id),
        HomeEntityKind.Artist => DetailNavigationArgs.Artist(Item.Id),
        _ => null,
    };

    public IAsyncRelayCommand PlayCommand { get; }

    /// <summary>Automation name for the row's play button.</summary>
    public string PlayName => Res.Get("Play");

    private static HomeEntityKind Classify(SearchItem item)
    {
        string type = item.Type ?? string.Empty;
        if (type.Contains("song", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Song;
        }

        if (type.Contains("album", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Album;
        }

        if (type.Contains("playlist", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Playlist;
        }

        if (type.Contains("artist", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Artist;
        }

        // Shape fallback: top-query items carry the song ids they resolve to.
        return !string.IsNullOrWhiteSpace(item.SongIds)
            ? HomeEntityKind.Song
            : HomeEntityKind.DisplayOnly;
    }

    private async Task PlayAsync()
    {
        // Lightweight item → resolve to a playable Song first (§5.2),
        // preferring an explicit song_ids payload when present.
        string id = !string.IsNullOrWhiteSpace(Item.SongIds)
            ? Item.SongIds.Split(',')[0].Trim()
            : Item.Id;
        var songs = await _client.GetSongsByIdsAsync(new[] { id });
        if (songs.Count == 0)
        {
            return;
        }

        Song song = songs[0];
        await _playback.PlayAsync(song, new[] { song });
        try
        {
            await _store.RecordPlayAsync(song);
        }
        catch (Exception)
        {
            // History is best-effort; playback already started.
        }
    }
}
