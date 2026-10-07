namespace Omega.Core.Models;

/// <summary>
/// A favorited non-song entity (album, playlist or artist) in the local
/// library. <see cref="Kind"/> is the upstream entity kind
/// (<c>"album"</c> / <c>"playlist"</c> / <c>"artist"</c>); the remaining
/// fields are a render snapshot captured at favorite time, so the
/// Library page can draw the entry offline and navigate back to the
/// entity's detail page from <see cref="Kind"/> + <see cref="Id"/>
/// (or <see cref="Url"/> when an id is unavailable).
/// </summary>
public sealed record FavoriteEntity(
    string Kind,
    string Id,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    string? Url);
