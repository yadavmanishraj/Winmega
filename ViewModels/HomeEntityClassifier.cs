using System;
using Omega.Core.Models;

namespace Omega.ViewModels;

/// <summary>The actionable shapes a Home browse entity can take.</summary>
public enum HomeEntityKind
{
    Song,
    Album,
    Playlist,
    Artist,

    /// <summary>Radio stations, shows and anything unrecognised — display-only in v1 (design §9.2).</summary>
    DisplayOnly,
}

/// <summary>
/// Classifies Home entities by SHAPE, never by the raw upstream
/// <c>type</c> value (UPSTREAM_VALIDATION §4: entity <c>type</c> inside
/// browse sections is unreliable — the same Android rule). Shape keys,
/// in order: an embedded track list means album-shaped; otherwise the
/// perma-URL path segment names the entity kind (<c>/song/</c>,
/// <c>/album/</c>, <c>/playlist/</c> or <c>/featured/</c> for curated
/// playlists, <c>/artist/</c>).
/// </summary>
public static class HomeEntityClassifier
{
    public static HomeEntityKind Classify(HomeEntity entity)
    {
        if (entity.Tracks.Count > 0)
        {
            return HomeEntityKind.Album;
        }

        string url = entity.Url ?? string.Empty;
        if (url.Contains("/song/", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Song;
        }

        if (url.Contains("/album/", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Album;
        }

        if (url.Contains("/playlist/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/featured/", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Playlist;
        }

        if (url.Contains("/artist/", StringComparison.OrdinalIgnoreCase))
        {
            return HomeEntityKind.Artist;
        }

        return HomeEntityKind.DisplayOnly;
    }
}
