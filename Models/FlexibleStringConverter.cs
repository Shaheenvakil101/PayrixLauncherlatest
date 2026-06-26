using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

/// <summary>
/// Accepts JSON strings, numbers, booleans, and nulls — always deserializes to string?.
/// Needed because some Payrix fields (e.g. "funded") return a number in production
/// but a string (or null) in sandbox.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null          => null,
            JsonTokenType.String        => reader.GetString(),
            JsonTokenType.Number        => reader.TryGetDecimal(out var d) ? d.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True          => "true",
            JsonTokenType.False         => "false",
            _                           => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// Accepts JSON numbers OR quoted number strings for int? fields.
/// Needed because some Payrix fields (e.g. lineItemDetailIndicator, discountTreatment)
/// are sometimes returned as "1" (string) rather than 1 (number).
/// </summary>
/// <summary>Handles non-nullable int that Payrix sometimes returns as string "1" or "2".</summary>
public class FlexibleIntToNonNullableConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var i) ? i : 0,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var p) ? p : 0,
            JsonTokenType.True   => 1,
            JsonTokenType.False  => 0,
            JsonTokenType.Null   => 0,
            _                    => 0
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var i) ? i : (int?)null;
            case JsonTokenType.String:
                var s = reader.GetString();
                return int.TryParse(s, out var parsed) ? parsed : null;
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
