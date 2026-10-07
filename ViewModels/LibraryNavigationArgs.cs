namespace Omega.ViewModels;

/// <summary>
/// Frame navigation parameter for <c>LibraryPage</c> (Apple-layout shell).
/// <see cref="Tab"/> is one of <c>"favorites"</c>, <c>"playlists"</c>,
/// <c>"history"</c>, <c>"downloads"</c> — the page's existing tab kinds.
/// <see cref="PlaylistId"/> optionally preselects a user playlist when
/// <see cref="Tab"/> is <c>"playlists"</c> (sidebar Playlists section).
/// </summary>
public sealed record LibraryNavigationArgs(string Tab, string? PlaylistId = null)
{
    public static LibraryNavigationArgs Favorites() => new("favorites");

    public static LibraryNavigationArgs Playlists(string? playlistId = null) => new("playlists", playlistId);

    public static LibraryNavigationArgs History() => new("history");

    public static LibraryNavigationArgs Downloads() => new("downloads");
}
