using System.Text.Json;
using System.Text.Json.Serialization;
using Omega.Core.Upstream.Dtos;

namespace Omega.Core.Upstream;

/// <summary>
/// The ONLY JSON entry point in Core. All (de)serialization goes through
/// this System.Text.Json source-generated context — reflection-based
/// serialization is banned project-wide (Native AOT requirement).
/// Every root payload type the client consumes is registered here.
/// Property names come from [JsonPropertyName] attributes on the DTOs
/// (upstream is snake_case); unknown keys are skipped by default.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RawSongDetailsDto))]
[JsonSerializable(typeof(RawSongDto))]
[JsonSerializable(typeof(List<RawSongDto>))]
[JsonSerializable(typeof(RawPagedDto<RawSongDto>))]
[JsonSerializable(typeof(RawPagedDto<RawAlbumDto>))]
[JsonSerializable(typeof(RawPagedDto<RawArtistMapDto>))]
[JsonSerializable(typeof(RawPagedDto<RawPlaylistDto>))]
[JsonSerializable(typeof(RawAlbumDto))]
[JsonSerializable(typeof(RawPlaylistDto))]
[JsonSerializable(typeof(RawArtistPageDto))]
[JsonSerializable(typeof(RawArtistSongsDto))]
[JsonSerializable(typeof(RawArtistAlbumsDto))]
[JsonSerializable(typeof(RawGlobalSearchDto))]
[JsonSerializable(typeof(RawLyricsDto))]
[JsonSerializable(typeof(RawStationCreatedDto))]
[JsonSerializable(typeof(RawStationEntryDto))]
[JsonSerializable(typeof(RawBrowseItemDto))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class UpstreamJsonContext : JsonSerializerContext;
