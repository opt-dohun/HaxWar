namespace HexWar.Infrastructure.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 도메인 객체 직렬화를 위한 표준 옵션
/// </summary>
public static class DomainJsonOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // 순환 참조 방지
            ReferenceHandler = ReferenceHandler.IgnoreCycles,

            // 최대 깊이
            MaxDepth = 64
        };

        // 커스텀 컨버터 등록
        options.Converters.Add(new NodeIdJsonConverter());
        options.Converters.Add(new PlayerIdJsonConverter());
        options.Converters.Add(new EdgeIdJsonConverter());
        options.Converters.Add(new DistanceJsonConverter());

        return options;
    }
}