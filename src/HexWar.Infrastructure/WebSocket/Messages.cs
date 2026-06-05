namespace HexWar.Infrastructure.WebSocket;

using System.Text.Json;
using System.Text.Json.Serialization;
using HexWar.Domain.Events;

public class ClientMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public class ServerMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("round")]
    public int? Round { get; set; }

    public static ServerMessage FromDomainEvent(IDomainEvent domainEvent, string roomId, int currentRound)
    {
        return new ServerMessage
        {
            Type = "game_event",
            EventType = domainEvent.GetType().Name,
            Payload = domainEvent,
            Timestamp = DateTime.UtcNow,
            Round = currentRound
        };
    }

    public static ServerMessage FromDto<T>(T dto, string eventType, int? currentRound = null)
    {
        return new ServerMessage
        {
            Type = "game_event",
            EventType = eventType,
            Payload = dto,
            Timestamp = DateTime.UtcNow,
            Round = currentRound
        };
    }
}


public static class ClientMessageTypes
{
    public const string MoveUnits = "move_units";
    public const string EncounterDecision = "encounter_decision";
    public const string GetState = "get_state";
    public const string Ping = "ping";
}

public static class ServerMessageTypes
{
    public const string GameEvent = "game_event";
    public const string StateUpdate = "state_update";
    public const string Error = "error";
    public const string Pong = "pong";
}

public class MoveUnitsPayload
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class EncounterDecisionPayload
{
    [JsonPropertyName("from_node")]
    public int FromNode { get; set; }

    [JsonPropertyName("to_node")]
    public int ToNode { get; set; }

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}