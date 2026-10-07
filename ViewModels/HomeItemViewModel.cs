using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;

namespace Omega.ViewModels;

/// <summary>
/// One Home browse entity (hero / trending row / tile). Classification
/// is by shape (see <see cref="HomeEntityClassifier"/>); the page
/// navigates for album/playlist/artist items and lets this row's Play
/// command handle song-shaped and track-carrying items.
/// </summary>
public partial class HomeItemViewModel : ObservableObject
{
    private readonly Func<HomeItemViewModel, Task> _playHandler;

    public HomeItemViewModel(HomeEntity entity, Func<HomeItemViewModel, Task> playHandler)
    {
        Entity = entity;
        _playHandler = playHandler;
        Kind = HomeEntityClassifier.Classify(entity);
    }

    public HomeEntity Entity { get; }

    public HomeEntityKind Kind { get; }

    public string Title => Entity.Title;

    public string Subtitle => Entity.Subtitle ?? string.Empty;

    public ImageSource? Artwork => ArtworkHelper.From(Entity.Image.Medium ?? Entity.Image.Small);

    public ImageSource? LargeArtwork => ArtworkHelper.From(Entity.Image.Large ?? Entity.Image.Medium);

    /// <summary>
    /// Originating surface hint forwarded into <see cref="DetailArgs"/>
    /// (e.g. "charts" for Home's Charts section, set by HomeViewModel's
    /// fill); null everywhere else.
    /// </summary>
    public string? NavigationSource { get; set; }

    /// <summary>Songs play directly; albums with an embedded track list play their tracks.</summary>
    public bool IsPlayable =>
        Kind == HomeEntityKind.Song ||
        (Kind == HomeEntityKind.Album && Entity.Tracks.Count > 0);

    public bool IsNavigable =>
        Kind is HomeEntityKind.Album or HomeEntityKind.Playlist or HomeEntityKind.Artist;

    /// <summary>
    /// Detail args for navigable items; null for songs and
    /// display-only items. Carries the card's title/subtitle/artwork
    /// as the detail header's instant preview. When the entity has no
    /// usable id but does carry a perma-URL, the args take the
    /// link-token form (album/playlist only — there is no artist
    /// link-token call); with neither, the item is not navigable.
    /// </summary>
    public DetailNavigationArgs? DetailArgs => Kind switch
    {
        HomeEntityKind.Album => BuildArgs("album"),
        HomeEntityKind.Playlist => BuildArgs("playlist"),
        HomeEntityKind.Artist => BuildArgs("artist"),
        _ => null,
    };

    private DetailNavigationArgs? BuildArgs(string kind)
    {
        string? image = Entity.Image.Large ?? Entity.Image.Medium;
        if (!string.IsNullOrWhiteSpace(Entity.Id))
        {
            return new DetailNavigationArgs(kind, Entity.Id, Entity.Title, Entity.Subtitle, image, Source: NavigationSource);
        }

        string? token = ExtractLinkToken(Entity.Url);
        if (token is null || kind == "artist")
        {
            return null;
        }

        return new DetailNavigationArgs(kind, string.Empty, Entity.Title, Entity.Subtitle, image, Token: token, Source: NavigationSource);
    }

    /// <summary>The link token is the last path segment of a JioSaavn perma-URL.</summary>
    private static string? ExtractLinkToken(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmed = url.Trim().TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        if (slash < 0 || slash == trimmed.Length - 1)
        {
            return null;
        }

        string token = trimmed[(slash + 1)..];
        int cut = token.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            token = token[..cut];
        }

        return token.Length > 0 ? token : null;
    }

    /// <summary>Automation name for the row's play button.</summary>
    public string PlayName => Res.Get("Play");

    [RelayCommand]
    private Task PlayAsync() => _playHandler(this);
}
