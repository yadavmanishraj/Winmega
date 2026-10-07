using System.Text.Json.Serialization;

namespace Omega.Core.Upstream.Dtos;

// Lyrics (lyrics.getLyrics). Per UPSTREAM_VALIDATION §3 the request's
// lyrics_id parameter is the SONG ID. A body without a "lyrics" key
// (e.g. {"status":"failure","error":{...}}) means "no lyrics".

public sealed class RawLyricsDto
{
    [JsonPropertyName("lyrics")] public string? Lyrics { get; set; }
    [JsonPropertyName("lyrics_copyright")] public string? LyricsCopyright { get; set; }
    [JsonPropertyName("snippet")] public string? Snippet { get; set; }
}

// Radio / suggestions (UPSTREAM_SPEC §4.9). Step 1 creates a station;
// step 2's response is an object with a "stationid" key plus
// numeric-string keys whose values are { song: {...} } — handled via a
// Dictionary<string, JsonElement> in the client, because upstream
// currently answers step 2 with {"stationid": ..., "error": "..."}
// (VALIDATION §6), which must not break deserialization.

public sealed class RawStationCreatedDto
{
    [JsonPropertyName("stationid")] public string? StationId { get; set; }
}

public sealed class RawStationEntryDto
{
    [JsonPropertyName("song")] public RawSongDto? Song { get; set; }
}

// Browse items (content.getBrowseModules / content.getTrending items).
// The section STRUCTURE is not modelled as DTOs: per design §3.2 the
// modules payload is walked manually as JsonElement (five sections are
// direct arrays; "radio" wraps items in featured_stations and
// "top_shows" in shows; entity "type" is unreliable — branch on shape).
// Only the per-item shape below goes through the typed DTO + context.

public sealed class RawBrowseItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("header_desc")] public string? HeaderDesc { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("perma_url")] public string? PermaUrl { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }

    // Present on album-shaped items (their tracks). Entity "type" values
    // inside sections are unreliable (VALIDATION §4) — branch on this.
    // Upstream serves "" instead of an array when no tracks are inlined
    // (verified live), so the tolerant song-list converter is required.
    [JsonPropertyName("list")]
    [JsonConverter(typeof(FlexibleSongListConverter))]
    public List<RawSongDto>? List { get; set; }
}
