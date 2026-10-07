using System.Text.Json.Serialization;

namespace Omega.Core.Upstream.Dtos;

// Album details (content.getAlbumDetails, UPSTREAM_SPEC §4.7) and album
// search results share this shape; more_info is the union of both
// variants' fields, all optional.

public sealed class RawAlbumMoreInfoDto
{
    [JsonPropertyName("artistMap")] public RawArtistMapGroupDto? ArtistMap { get; set; }
    [JsonPropertyName("song_count")] public string? SongCount { get; set; }
    [JsonPropertyName("copyright_text")] public string? CopyrightText { get; set; }

    [JsonPropertyName("is_dolby_content")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsDolbyContent { get; set; }

    [JsonPropertyName("label_url")] public string? LabelUrl { get; set; }

    // Search-result-only extras.
    [JsonPropertyName("music")] public string? Music { get; set; }
    [JsonPropertyName("query")] public string? Query { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public sealed class RawAlbumDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("header_desc")] public string? HeaderDesc { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("year")] public string? Year { get; set; }
    [JsonPropertyName("play_count")] public string? PlayCount { get; set; }
    [JsonPropertyName("explicit_content")] public string? ExplicitContent { get; set; }

    // The true track total (VALIDATION §5); a String upstream.
    [JsonPropertyName("list_count")] public string? ListCount { get; set; }

    // "" instead of an array when upstream inlines no tracks — tolerant
    // song-list converter (same quirk as browse items).
    [JsonPropertyName("list")]
    [JsonConverter(typeof(FlexibleSongListConverter))]
    public List<RawSongDto> List { get; set; } = new();
    [JsonPropertyName("more_info")] public RawAlbumMoreInfoDto? MoreInfo { get; set; }
}

// Playlist details (playlist.getDetails, UPSTREAM_SPEC §4.10) and
// playlist search results share this shape.

public sealed class RawPlaylistMoreInfoDto
{
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("firstname")] public string? Firstname { get; set; }
    [JsonPropertyName("lastname")] public string? Lastname { get; set; }
    [JsonPropertyName("song_count")] public string? SongCount { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("follower_count")] public string? FollowerCount { get; set; }
    [JsonPropertyName("fan_count")] public string? FanCount { get; set; }

    [JsonPropertyName("is_dolby_content")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsDolbyContent { get; set; }

    [JsonPropertyName("artists")] public List<RawArtistMapDto> Artists { get; set; } = new();
}

public sealed class RawPlaylistDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("header_desc")] public string? HeaderDesc { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("year")] public string? Year { get; set; }
    [JsonPropertyName("play_count")] public string? PlayCount { get; set; }
    [JsonPropertyName("explicit_content")] public string? ExplicitContent { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    // The true song total (VALIDATION §5); a String upstream. The
    // returned list is only the requested page.
    [JsonPropertyName("list_count")] public string? ListCount { get; set; }

    // "" instead of an array when upstream inlines no songs — tolerant
    // song-list converter (same quirk as browse items).
    [JsonPropertyName("list")]
    [JsonConverter(typeof(FlexibleSongListConverter))]
    public List<RawSongDto> List { get; set; } = new();
    [JsonPropertyName("more_info")] public RawPlaylistMoreInfoDto? MoreInfo { get; set; }
}
