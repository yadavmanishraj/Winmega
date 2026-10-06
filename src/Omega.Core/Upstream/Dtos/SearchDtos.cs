using System.Text.Json.Serialization;

namespace Omega.Core.Upstream.Dtos;

// Global search (autocomplete.get, UPSTREAM_SPEC §4.1). Each section is
// { data: [...], position: Int }. The response also contains "episodes"
// and "shows" sections (VALIDATION §1) which the app does not surface.

public sealed class RawSectionDto<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; set; } = new();

    [JsonPropertyName("position")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Position { get; set; }
}

public sealed class RawGlobalSongMoreInfoDto
{
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("primary_artists")] public string? PrimaryArtists { get; set; }
    [JsonPropertyName("singers")] public string? Singers { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

public sealed class RawGlobalSongItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("more_info")] public RawGlobalSongMoreInfoDto? MoreInfo { get; set; }
}

public sealed class RawGlobalAlbumMoreInfoDto
{
    [JsonPropertyName("music")] public string? Music { get; set; }
    [JsonPropertyName("year")] public string? Year { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("song_pids")] public string? SongPids { get; set; }
}

public sealed class RawGlobalAlbumItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("more_info")] public RawGlobalAlbumMoreInfoDto? MoreInfo { get; set; }
}

public sealed class RawGlobalArtistItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    // This section's items carry position at item level and have NO perma_url.
    [JsonPropertyName("position")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Position { get; set; }
}

public sealed class RawGlobalPlaylistMoreInfoDto
{
    [JsonPropertyName("firstname")] public string? Firstname { get; set; }
    [JsonPropertyName("lastname")] public string? Lastname { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

public sealed class RawGlobalPlaylistItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("more_info")] public RawGlobalPlaylistMoreInfoDto? MoreInfo { get; set; }
}

public sealed class RawGlobalSearchDto
{
    [JsonPropertyName("topquery")] public RawSectionDto<RawGlobalSongItemDto>? TopQuery { get; set; }
    [JsonPropertyName("songs")] public RawSectionDto<RawGlobalSongItemDto>? Songs { get; set; }
    [JsonPropertyName("albums")] public RawSectionDto<RawGlobalAlbumItemDto>? Albums { get; set; }
    [JsonPropertyName("artists")] public RawSectionDto<RawGlobalArtistItemDto>? Artists { get; set; }
    [JsonPropertyName("playlists")] public RawSectionDto<RawGlobalPlaylistItemDto>? Playlists { get; set; }
}
