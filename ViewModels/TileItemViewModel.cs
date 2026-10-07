using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;

namespace Omega.ViewModels;

/// <summary>
/// One navigable entity tile (album / playlist / artist), shared by
/// Search result tabs and the Detail page's artist rails. Immutable —
/// no change notification needed.
/// </summary>
public sealed class TileItemViewModel
{
    public TileItemViewModel(string kind, string id, string title, string? subtitle, string? imageUrl)
    {
        Kind = kind;
        Id = id;
        Title = title;
        Subtitle = subtitle ?? string.Empty;
        Artwork = ArtworkHelper.From(imageUrl);
    }

    public string Kind { get; }

    public string Id { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public ImageSource? Artwork { get; }

    public DetailNavigationArgs DetailArgs => new(Kind, Id);

    public static TileItemViewModel FromAlbum(Album album)
    {
        string artists = string.Join(", ", album.Artists.Primary.Select(a => a.Name));
        string subtitle = artists.Length > 0
            ? artists
            : album.Year?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        return new TileItemViewModel(
            "album", album.Id, album.Name, subtitle, album.Image.Medium ?? album.Image.Small);
    }

    public static TileItemViewModel FromPlaylist(Playlist playlist) => new(
        "playlist",
        playlist.Id,
        playlist.Name,
        playlist.OwnerName ?? Res.Format("PlaylistSongCountFormat", playlist.SongCount),
        playlist.Image.Medium ?? playlist.Image.Small);

    public static TileItemViewModel FromArtistRef(ArtistRef artist) => new(
        "artist",
        artist.Id,
        artist.Name,
        null,
        artist.Image.Medium ?? artist.Image.Small);

    public static TileItemViewModel FromSearchItem(SearchItem item, string kind) => new(
        kind,
        item.Id,
        item.Title,
        item.Description ?? item.PrimaryArtistsText,
        item.Image.Medium ?? item.Image.Small);
}
