namespace Omega.Core.Models;

/// <summary>An album with its full track list (album details are not paged upstream).</summary>
public sealed record Album(
    string Id,
    string Name,
    string? Description,
    string? Url,
    ImageSet Image,
    string? Language,
    int? Year,
    long? PlayCount,
    bool Explicit,
    int SongCount,
    ArtistGroups Artists,
    IReadOnlyList<Song> Songs);
