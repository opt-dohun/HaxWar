namespace HexWar.Infrastructure.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using HexWar.Domain.ValueObjects;

/// <summary>
/// NodeId의 JSON 직렬화/역직렬화를 처리합니다.
/// JSON: 1 → NodeId(1)
/// </summary>
public class NodeIdJsonConverter : JsonConverter<NodeId>
{
    public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new NodeId(reader.GetInt32());
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

                    if (propName?.ToLower() == "value")
                    {
                        value = reader.GetInt32();
                    }
                }
            }
            return new NodeId(value);
        }

        throw new JsonException($"Unexpected token type for NodeId: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }

    public override NodeId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        if (str != null)
        {
            if (str.StartsWith('N') || str.StartsWith('n'))
            {
                str = str.Substring(1);
            }
            if (int.TryParse(str, out var val))
            {
                return new NodeId(val);
            }
        }
        throw new JsonException($"Cannot deserialize '{str}' as NodeId property name");
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}