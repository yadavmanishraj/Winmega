using System.Text.Json.Serialization;

namespace Omega.Core.Persistence;

/// <summary>
/// Source-generated JSON context for everything Core persists itself
/// (song snapshots inside the SQLite library tables). Kept separate from
/// <c>UpstreamJsonContext</c> (wire payloads): no type is ever serialized
/// through two mechanisms (design §3.2). Property names are snake_case,
/// matching the upstream naming the snapshot fields mirror.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SongSnapshot))]
[JsonSerializable(typeof(ArtistSnapshot))]
[JsonSerializable(typeof(StreamSnapshot))]
public partial class PersistenceJsonContext : JsonSerializerContext;
