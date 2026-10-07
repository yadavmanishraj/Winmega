namespace Omega.ViewModels;

/// <summary>
/// Frame navigation parameter for <c>DetailPage</c> (design §9.5).
/// <see cref="Kind"/> is one of <c>"album"</c>, <c>"playlist"</c>,
/// <c>"artist"</c> — the upstream entity kinds the page can render.
/// </summary>
public sealed record DetailNavigationArgs(string Kind, string Id)
{
    public static DetailNavigationArgs Album(string id) => new("album", id);

    public static DetailNavigationArgs Playlist(string id) => new("playlist", id);

    public static DetailNavigationArgs Artist(string id) => new("artist", id);
}
