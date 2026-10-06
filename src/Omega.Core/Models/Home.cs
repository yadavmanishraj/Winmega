namespace Omega.Core.Models;

/// <summary>
/// One entity inside a browse-modules (Home) section. Upstream mixes
/// entity types inside sections and the raw <c>type</c> value is
/// unreliable (UPSTREAM_VALIDATION §4), so classification is by shape:
/// an item carrying a track <c>list</c> is album-shaped and its tracks
/// are exposed via <see cref="Tracks"/>.
/// </summary>
public sealed record HomeEntity(
    string Id,
    string Title,
    string? Subtitle,
    string? HeaderDescription,
    string RawType,
    string? Url,
    ImageSet Image,
    string? Language,
    IReadOnlyList<Song> Tracks)
{
    /// <summary>True when the item carried an embedded track list (album-shaped).</summary>
    public bool HasTrackList => Tracks.Count > 0;
}

/// <summary>A named Home section (e.g. "new_trending") with its entities.</summary>
public sealed record HomeSection(string Key, IReadOnlyList<HomeEntity> Items);

/// <summary>
/// The Home feed: upstream <c>content.getBrowseModules</c>. Five of the
/// seven sections are direct top-level arrays; <c>radio</c> and
/// <c>top_shows</c> are wrapper objects (UPSTREAM_VALIDATION §4).
/// </summary>
public sealed record HomeModules(
    IReadOnlyList<HomeEntity> NewTrending,
    IReadOnlyList<HomeEntity> NewAlbums,
    IReadOnlyList<HomeEntity> Charts,
    IReadOnlyList<HomeEntity> TopPlaylists,
    IReadOnlyList<HomeEntity> BrowseDiscover,
    IReadOnlyList<HomeEntity> RadioStations,
    IReadOnlyList<HomeEntity> TopShows)
{
    /// <summary>All sections in display order, for UIs that render sections generically.</summary>
    public IReadOnlyList<HomeSection> Sections => new[]
    {
        new HomeSection("new_trending", NewTrending),
        new HomeSection("new_albums", NewAlbums),
        new HomeSection("charts", Charts),
        new HomeSection("top_playlists", TopPlaylists),
        new HomeSection("browse_discover", BrowseDiscover),
        new HomeSection("radio", RadioStations),
        new HomeSection("top_shows", TopShows),
    };
}
