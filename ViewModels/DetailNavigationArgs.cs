namespace Omega.ViewModels;

/// <summary>
/// Frame navigation parameter for <c>DetailPage</c> (design §9.5,
/// DETAIL_PAGE_DESIGN §1.1). <see cref="Kind"/> is one of
/// <c>"album"</c>, <c>"playlist"</c>, <c>"artist"</c> — the upstream
/// entity kinds the page can render.
///
/// Two addressing forms: by <see cref="Id"/> (the common case), or by
/// <see cref="Token"/> — the link token from an entity's perma-URL,
/// used when a browse item carries a URL but no usable id; the page
/// resolves it through the client's <c>*ByLinkTokenAsync</c> calls.
///
/// The optional preview fields (<see cref="Title"/>,
/// <see cref="Subtitle"/>, <see cref="ImageUrl"/>) carry what the
/// caller already knows from the card that was tapped, so the detail
/// header paints instantly and the detail load fills in behind it.
///
/// <see cref="Source"/> names the originating surface (e.g.
/// <c>"charts"</c> for Home's Charts section) when the caller knows
/// it; the page uses it to pick the CHART eyebrow + ranked rows for
/// chart playlists. Callers that don't know it leave it null and the
/// page falls back to the plain playlist treatment.
/// </summary>
public sealed record DetailNavigationArgs(
    string Kind,
    string Id,
    string? Title = null,
    string? Subtitle = null,
    string? ImageUrl = null,
    string? Token = null,
    string? Source = null)
{
    public static DetailNavigationArgs Album(string id) => new("album", id);

    public static DetailNavigationArgs Playlist(string id) => new("playlist", id);

    public static DetailNavigationArgs Artist(string id) => new("artist", id);

    /// <summary>Album addressed by its share-link token instead of an id.</summary>
    public static DetailNavigationArgs AlbumByToken(string token) =>
        new("album", string.Empty, Token: token);

    /// <summary>Playlist addressed by its share-link token instead of an id.</summary>
    public static DetailNavigationArgs PlaylistByToken(string token) =>
        new("playlist", string.Empty, Token: token);

    /// <summary>True when the caller marked this playlist as a chart.</summary>
    public bool IsChart => Source == "charts";
}
