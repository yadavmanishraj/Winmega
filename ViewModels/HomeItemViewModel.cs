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

    /// <summary>Songs play directly; albums with an embedded track list play their tracks.</summary>
    public bool IsPlayable =>
        Kind == HomeEntityKind.Song ||
        (Kind == HomeEntityKind.Album && Entity.Tracks.Count > 0);

    public bool IsNavigable =>
        Kind is HomeEntityKind.Album or HomeEntityKind.Playlist or HomeEntityKind.Artist;

    /// <summary>Detail args for navigable items; null for songs and display-only items.</summary>
    public DetailNavigationArgs? DetailArgs => Kind switch
    {
        HomeEntityKind.Album => DetailNavigationArgs.Album(Entity.Id),
        HomeEntityKind.Playlist => DetailNavigationArgs.Playlist(Entity.Id),
        HomeEntityKind.Artist => DetailNavigationArgs.Artist(Entity.Id),
        _ => null,
    };

    /// <summary>Automation name for the row's play button.</summary>
    public string PlayName => Res.Get("Play");

    [RelayCommand]
    private Task PlayAsync() => _playHandler(this);
}
