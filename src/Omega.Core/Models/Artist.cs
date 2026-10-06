namespace Omega.Core.Models;

/// <summary>An artist page: profile fields plus top songs/albums, singles and similar artists.</summary>
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
    IReadOnlyList<Song> Singles,
    IReadOnlyList<ArtistRef> SimilarArtists);
