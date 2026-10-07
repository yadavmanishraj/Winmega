using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omega.Core.Upstream.Dtos;

namespace Omega.Core.Upstream;

/// <summary>
/// Upstream is stringly-typed and inconsistent: paged <c>total</c>/<c>start</c>
/// are JSON numbers in the songs model but string-coerced elsewhere
/// (UPSTREAM_SPEC §8.2). These converters accept either form.
/// They are plain <see cref="JsonConverter{T}"/> implementations, so they
/// work unchanged inside the source-generated JSON context (no reflection).
/// </summary>
public sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int i) ? i : (int)reader.GetDouble();
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
                    ? s
                    : 0;
            case JsonTokenType.Null:
                return 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>Long variant of <see cref="FlexibleInt32Converter"/>.</summary>
public sealed class FlexibleInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long l) ? l : (long)reader.GetDouble();
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)
                    ? s
                    : 0;
            case JsonTokenType.Null:
                return 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>
/// Accepts real JSON booleans plus the string/number encodings upstream
/// occasionally emits ("true"/"false"/"1"/"0", 1/0).
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return false;
            case JsonTokenType.String:
                string? s = reader.GetString();
                return s == "true" || s == "1";
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long n) && n != 0;
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>
/// Song lists are not always arrays upstream: browse items on albums and
/// trending entries serve <c>"list": ""</c> (an empty string) when no
/// tracks are inlined — verified live on <c>content.getBrowseModules</c> —
/// and empty albums/playlists can plausibly do the same. This converter
/// maps any non-array token (string, object, number, boolean, null) to an
/// empty list and reads only real arrays, element by element, so a
/// hostile "list" shape can never fail deserialization of the whole
/// payload. An empty list matches every consumer's existing semantics:
/// the mapper coalesces null/absent track lists to empty, and album/
/// playlist totals come from <c>list_count</c>, never the page size.
/// Null is routed through the converter (<see cref="HandleNull"/>) so a
/// JSON null cannot null out the album/playlist DTOs' non-null list.
/// </summary>
public sealed class FlexibleSongListConverter : JsonConverter<List<RawSongDto>>
{
    public override bool HandleNull => true;

    public override List<RawSongDto> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // "", {}, numbers, booleans, null: no inlined tracks. Skip()
            // consumes the whole token (object included); the reader ends
            // positioned on the value's last token, as the contract wants.
            reader.Skip();
            return new List<RawSongDto>();
        }

        var songs = new List<RawSongDto>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return songs;
                case JsonTokenType.StartObject:
                    // JsonTypeInfo overload (source-generated) — the
                    // options-based generic overload is RUC/RDC-marked and
                    // trips IL2026/IL3050 even when the options ARE the
                    // source-gen context.
                    RawSongDto? song = JsonSerializer.Deserialize(ref reader, UpstreamJsonContext.Default.RawSongDto);
                    if (song is not null)
                    {
                        songs.Add(song);
                    }

                    break;
                default:
                    // Stray scalar/null element: skip it rather than
                    // failing the whole payload.
                    reader.Skip();
                    break;
            }
        }

        return songs;
    }

    public override void Write(Utf8JsonWriter writer, List<RawSongDto> value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (RawSongDto song in value)
        {
            JsonSerializer.Serialize(writer, song, UpstreamJsonContext.Default.RawSongDto);
        }

        writer.WriteEndArray();
    }
}
