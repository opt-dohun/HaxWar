namespace HexWar.Infrastructure.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using HexWar.Domain.ValueObjects;

/// <summary>
/// EdgeId의 JSON 직렬화/역직렬화를 처리합니다.
/// JSON: "1-2" → EdgeId(NodeId(1), NodeId(2))
/// </summary>
public class EdgeIdJsonConverter : JsonConverter<EdgeId>
{
    public override EdgeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var key = reader.GetString()!;
            var parts = key.Split('-');
            return new EdgeId(
                new NodeId(int.Parse(parts[0])),
                new NodeId(int.Parse(parts[1])));
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            int from = 0, to = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propName = reader.GetString();
                    reader.Read();

                    if (propName?.ToLower() == "from")
                    {
                        from = reader.TokenType == JsonTokenType.Number
                            ? reader.GetInt32()
                            : int.Parse(reader.GetString()!);
                    }
                    else if (propName?.ToLower() == "to")
                    {
                        to = reader.TokenType == JsonTokenType.Number
                            ? reader.GetInt32()
                            : int.Parse(reader.GetString()!);
                    }
                }
            }
            return new EdgeId(new NodeId(from), new NodeId(to));
        }

        throw new JsonException($"Unexpected token type for EdgeId: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, EdgeId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Key);
    }

    public override EdgeId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        if (str == null) throw new JsonException("Null edge ID");
        var parts = str.Split('-');
        return new EdgeId(
            new NodeId(int.Parse(parts[0])),
            new NodeId(int.Parse(parts[1])));
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, EdgeId value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.Key);
    }
}