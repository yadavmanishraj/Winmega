using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

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
