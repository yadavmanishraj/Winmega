using System.Text.Json.Serialization;

namespace Omega.Core.Upstream.Dtos;

// Raw upstream song shape (UPSTREAM_SPEC §3.1 / §4.6). Upstream omits
// declared fields and adds undeclared ones, so EVERY DTO field is
// nullable or defaulted, and unmapped keys are ignored.

public sealed class RawArtistMapDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }

    // Present on artist-search items; ignored elsewhere.
    [JsonPropertyName("ctr")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long Ctr { get; set; }

    [JsonPropertyName("entity")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long Entity { get; set; }

    [JsonPropertyName("isRadioPresent")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsRadioPresent { get; set; }
}

public sealed class RawArtistMapGroupDto
{
    [JsonPropertyName("primary_artists")] public List<RawArtistMapDto> PrimaryArtists { get; set; } = new();
    [JsonPropertyName("featured_artists")] public List<RawArtistMapDto> FeaturedArtists { get; set; } = new();
    [JsonPropertyName("artists")] public List<RawArtistMapDto> Artists { get; set; } = new();
}

public sealed class RawSongMoreInfoDto
{
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }

    // NOTE (UPSTREAM_VALIDATION §3): in song.getDetails payloads this is
    // "false" even for songs that have lyrics, and LyricsId is absent.
    // Search payloads carry the accurate flag. Never gate lyrics on the
    // details payload — the client always calls lyrics with the song id.
    [JsonPropertyName("has_lyrics")] public string? HasLyrics { get; set; }
    [JsonPropertyName("lyrics_id")] public string? LyricsId { get; set; }
    [JsonPropertyName("copyright_text")] public string? CopyrightText { get; set; }
    [JsonPropertyName("album_id")] public string? AlbumId { get; set; }
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("album_url")] public string? AlbumUrl { get; set; }
    [JsonPropertyName("encrypted_media_url")] public string? EncryptedMediaUrl { get; set; }
    [JsonPropertyName("320kbps")] public string? Is320Kbps { get; set; }
    [JsonPropertyName("lyrics_snippet")] public string? LyricsSnippet { get; set; }
    [JsonPropertyName("artistMap")] public RawArtistMapGroupDto? ArtistMap { get; set; }
}

public sealed class RawSongDto
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

    // play_count lives at the TOP level of the song object (VALIDATION §1).
    [JsonPropertyName("play_count")] public string? PlayCount { get; set; }
    [JsonPropertyName("explicit_content")] public string? ExplicitContent { get; set; }
    [JsonPropertyName("more_info")] public RawSongMoreInfoDto? MoreInfo { get; set; }
}

/// <summary>Response of <c>song.getDetails</c>: exactly one key, <c>songs</c>.</summary>
public sealed class RawSongDetailsDto
{
    [JsonPropertyName("songs")] public List<RawSongDto> Songs { get; set; } = new();
}

/// <summary>
/// Paged envelope of the typed searches. <c>total</c>/<c>start</c> use the
/// flexible converter (number or numeric string). Do not page from
/// <c>start</c> — it can be negative (VALIDATION §1); use total + counts.
/// </summary>
public sealed class RawPagedDto<T>
{
    [JsonPropertyName("total")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Total { get; set; }

    [JsonPropertyName("start")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Start { get; set; }

    [JsonPropertyName("results")] public List<T> Results { get; set; } = new();
}
