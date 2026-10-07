namespace Omega.Core.Models;

/// <summary>
/// An artist page: profile fields plus top songs/albums, singles and
/// similar artists. <see cref="Singles"/> are album-lite releases
/// (upstream serves them as <c>type:"album"</c> items with no tracks
/// inlined), so they are <see cref="Album"/>s, not songs.
/// <see cref="LatestRelease"/> is likewise an album-lite item (null
/// when the payload carries no latest-release section).
/// </summary>
public sealed record Artist(
    string Id,
    string Name,
    string? Url,
    ImageSet Image,
    long? FollowerCount,
    long? FanCount,
    bool IsVerified,
    string? DominantLanguage,
    string? DominantType,
    IReadOnlyList<BioEntry> Bio,
    string? DateOfBirth,
    IReadOnlyList<Song> TopSongs,
    IReadOnlyList<Album> TopAlbums,
    IReadOnlyList<Album> Singles,
    IReadOnlyList<ArtistRef> SimilarArtists,
    string? Subtitle,
    Album? LatestRelease,
    IReadOnlyList<Playlist> DedicatedPlaylists,
    IReadOnlyList<Playlist> FeaturedPlaylists,
    IReadOnlyList<string> AvailableLanguages,
    string? Wiki);
