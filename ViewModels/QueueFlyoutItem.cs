using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;

namespace Omega.ViewModels;

/// <summary>
/// One "Up next" row in the shell strip's queue flyout. The flyout
/// lists the live queue MINUS the current track (the now-playing
/// block above the list carries that one), so rows are rebuilt from
/// the player queue state whenever the queue or the
/// current index changes — this wrapper is a display projection, not
/// state. The <see cref="Song"/> reference is what jump/remove act
/// on: the service tracks the queue by reference, so identity here
/// is exact even when the same song sits in the queue twice.
/// </summary>
public sealed class QueueFlyoutItem
{
    public QueueFlyoutItem(Song song)
    {
        Song = song;
        Artwork = ArtworkHelper.From(song.Image.Medium ?? song.Image.Small);
    }

    /// <summary>The queue entry this row represents.</summary>
    public Song Song { get; }

    public string Title => Song.Name;

    public string Artist => Song.PrimaryArtistNames;

    public string Duration => DisplayFormatting.Duration(Song.DurationSeconds);

    public ImageSource? Artwork { get; }
}
