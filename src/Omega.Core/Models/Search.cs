namespace Omega.Core.Models;

/// <summary>
/// One lightweight item of the global (autocomplete) search. These are
/// NOT playable — a tapped song must be resolved through song details
/// first (UPSTREAM_SPEC §4.1).
/// </summary>
public sealed record SearchItem(
    string Id,
    string Title,
    string Type,
    string? Url,
    ImageSet Image,
    string? Description,
    string? AlbumName,
    string? PrimaryArtistsText,
    string? SingersText,
    string? Language,
    string? Year,
    string? SongIds);

/// <summary>Global search sections (upstream <c>autocomplete.get</c>).</summary>
public sealed record SearchResults(
    IReadOnlyList<SearchItem> TopQuery,
    IReadOnlyList<SearchItem> Songs,
    IReadOnlyList<SearchItem> Albums,
    IReadOnlyList<SearchItem> Artists,
    IReadOnlyList<SearchItem> Playlists)
{
    public static readonly SearchResults Empty = new(
        Array.Empty<SearchItem>(), Array.Empty<SearchItem>(), Array.Empty<SearchItem>(),
        Array.Empty<SearchItem>(), Array.Empty<SearchItem>());
}

/// <summary>
/// One page of a typed search / paged listing. Paging decisions must be
/// based on <see cref="Total"/> and the accumulated item count — the
/// upstream <c>start</c> field is unreliable (UPSTREAM_VALIDATION §1).
/// </summary>
public sealed record PagedResult<T>(int Total, IReadOnlyList<T> Items)
{
    /// <summary>True when <paramref name="accumulatedCount"/> items cover the whole result set.</summary>
    public bool IsComplete(int accumulatedCount) => accumulatedCount >= Total;
}
