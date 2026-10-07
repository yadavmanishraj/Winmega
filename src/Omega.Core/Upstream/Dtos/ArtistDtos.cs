using System.Text.Json.Serialization;

namespace Omega.Core.Upstream.Dtos;

// Artist page (artist.getArtistPageDetails, UPSTREAM_SPEC §4.8).
// Only the fields the app maps are declared; everything else is ignored.

public sealed class RawArtistUrlsDto
{
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("songs")] public string? Songs { get; set; }
    [JsonPropertyName("albums")] public string? Albums { get; set; }
}

public sealed class RawSimilarArtistDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }

    // Note: similar artists use image_url, not image.
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class RawArtistPageDto
{
    // Both artistId and id are present upstream; mapper prefers artistId.
    [JsonPropertyName("artistId")] public string? ArtistId { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("follower_count")] public string? FollowerCount { get; set; }
    [JsonPropertyName("fan_count")] public string? FanCount { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("isVerified")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsVerified { get; set; }

    [JsonPropertyName("dominantLanguage")] public string? DominantLanguage { get; set; }
    [JsonPropertyName("dominantType")] public string? DominantType { get; set; }

    // A JSON-encoded string (array of {text, title, sequence}) — parsed
    // defensively in the mapper; a malformed bio must not kill the page.
    [JsonPropertyName("bio")] public string? Bio { get; set; }
    [JsonPropertyName("dob")] public string? Dob { get; set; }
    [JsonPropertyName("wiki")] public string? Wiki { get; set; }

    // Language codes the artist sings in; includes an "unknown" entry
    // the mapper filters out.
    [JsonPropertyName("availableLanguages")] public List<string> AvailableLanguages { get; set; } = new();
    [JsonPropertyName("urls")] public RawArtistUrlsDto? Urls { get; set; }
    [JsonPropertyName("topSongs")] public List<RawSongDto> TopSongs { get; set; } = new();
    [JsonPropertyName("topAlbums")] public List<RawAlbumDto> TopAlbums { get; set; } = new();

    // Album-lite items (type:"album", no tracks inlined) — same shape
    // as topAlbums.
    [JsonPropertyName("latest_release")] public List<RawAlbumDto> LatestRelease { get; set; } = new();

    // NOT songs: singles are album-lite releases (type:"album", no
    // encrypted media URL, no duration) — probe-verified. They map to
    // Albums, exactly like topAlbums.
    [JsonPropertyName("singles")] public List<RawAlbumDto> Singles { get; set; } = new();

    // Playlist-lite rails ("Just <Artist>" / "Featured In"); same
    // shape as playlist search results.
    [JsonPropertyName("dedicated_artist_playlist")] public List<RawPlaylistDto> DedicatedPlaylists { get; set; } = new();
    [JsonPropertyName("featured_artist_playlist")] public List<RawPlaylistDto> FeaturedPlaylists { get; set; } = new();
    [JsonPropertyName("similarArtists")] public List<RawSimilarArtistDto> SimilarArtists { get; set; } = new();
}

/// <summary>Response of <c>artist.getArtistMoreSong</c>.</summary>
public sealed class RawArtistSongsDto
{
    [JsonPropertyName("topSongs")] public RawArtistSongsPageDto? TopSongs { get; set; }
}

public sealed class RawArtistSongsPageDto
{
    [JsonPropertyName("songs")] public List<RawSongDto> Songs { get; set; } = new();

    [JsonPropertyName("total")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Total { get; set; }
}

/// <summary>Response of <c>artist.getArtistMoreAlbum</c>.</summary>
public sealed class RawArtistAlbumsDto
{
    [JsonPropertyName("topAlbums")] public RawArtistAlbumsPageDto? TopAlbums { get; set; }
}

public sealed class RawArtistAlbumsPageDto
{
    [JsonPropertyName("albums")] public List<RawAlbumDto> Albums { get; set; } = new();

    [JsonPropertyName("total")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Total { get; set; }
}
