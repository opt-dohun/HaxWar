// src/HexWar.Infrastructure/Serialization/DistanceJsonConverter.cs
namespace HexWar.Infrastructure.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using HexWar.Domain.ValueObjects;

/// <summary>
/// Distance의 JSON 직렬화/역직렬화를 처리합니다.
/// JSON: 2 → Distance(2)
/// </summary>
public class DistanceJsonConverter : JsonConverter<Distance>
{
    public override Distance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new Distance(reader.GetInt32());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            int value = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propName = reader.GetString();
                    reader.Read();

                    if (propName?.ToLower() == "roundsrequired")
                    {
                        value = reader.GetInt32();
                    }
                }
            }
            return new Distance(value);
        }

        throw new JsonException($"Unexpected token type for Distance: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, Distance value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.RoundsRequired);
    }
}