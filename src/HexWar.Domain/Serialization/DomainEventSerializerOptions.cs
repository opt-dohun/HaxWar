namespace HexWar.Domain.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;

public static class DomainEventSerializerOptions
{
    private static readonly JsonSerializerOptions _options;

    static DomainEventSerializerOptions()
    {
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 64
        };

        _options.Converters.Add(new NodeIdJsonConverter());
        _options.Converters.Add(new PlayerIdJsonConverter());
        _options.Converters.Add(new EdgeIdJsonConverter());
        _options.Converters.Add(new DistanceJsonConverter());
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public static JsonSerializerOptions Create() => _options;
}
