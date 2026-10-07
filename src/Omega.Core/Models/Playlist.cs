namespace Omega.Core.Models;

/// <summary>
/// A playlist. <see cref="SongCount"/> is the upstream <c>list_count</c>
/// (the true total — see UPSTREAM_VALIDATION §5); <see cref="Songs"/>
/// holds only the page that was fetched.
/// </summary>
public sealed record Playlist(
    string Id,
    string Name,
    string? Description,
    string? Url,
    ImageSet Image,
    string? Language,
    int? Year,
    long? PlayCount,
    int SongCount,
    string? OwnerName,
    IReadOnlyList<ArtistRef> Artists,
    IReadOnlyList<Song> Songs,
    long? FollowerCount,
    DateTimeOffset? LastUpdatedUtc,
    bool IsDolbyContent);
