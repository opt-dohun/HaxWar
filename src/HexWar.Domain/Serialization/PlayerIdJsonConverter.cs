namespace HexWar.Domain.Serialization;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexWar.Domain.ValueObjects;

public class PlayerIdJsonConverter : JsonConverter<PlayerId>
{
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new PlayerId(reader.GetString()!);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string value = string.Empty;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propName = reader.GetString();
                    reader.Read();

                    if (propName?.ToLower() == "value")
                    {
                        value = reader.GetString() ?? string.Empty;
                    }
                }
            }
            return new PlayerId(value);
        }

        throw new JsonException($"Unexpected token type for PlayerId: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }

    public override PlayerId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new PlayerId(reader.GetString() ?? string.Empty);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.Value);
    }
}
