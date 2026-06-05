// src/HexWar.Infrastructure/WebSocket/GameWebSocketHandler.cs
namespace HexWar.Infrastructure.WebSocket;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HexWar.Application.Sessions;
using HexWar.Domain.Commands;
using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;

/// <summary>
/// WebSocket 연결을 처리하고 GameSession으로 명령을 전달합니다.
/// </summary>
public class GameWebSocketHandler
{
    private readonly ConnectionManager _connectionManager;
    private readonly SessionRegistry _sessionRegistry;

    public GameWebSocketHandler(ConnectionManager connectionManager, SessionRegistry sessionRegistry)
    {
        _connectionManager = connectionManager;
        _sessionRegistry = sessionRegistry;
    }

    /// <summary>
    /// WebSocket 연결을 수락하고 메시지 루프를 시작합니다.
    /// </summary>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string roomId,
        string playerSide)
    {
        // 연결 등록
        _connectionManager.AddConnection(roomId, playerSide, webSocket);

        var session = _sessionRegistry.GetSession(roomId);
        if (session == null)
        {
            await SendErrorAsync(webSocket, "Session not found");
            return;
        }

        var side = Enum.Parse<PlayerSide>(playerSide);

        // 연결 알림
        await session.OnPlayerConnectedAsync(side);

        try
        {
            await ReceiveLoopAsync(webSocket, session, side);
        }
        finally
        {
            // 연결 해제
            await session.OnPlayerDisconnectedAsync(side);
            _connectionManager.RemoveConnection(webSocket);
        }
    }

    /// <summary>
    /// WebSocket 메시지 수신 루프
    /// </summary>
    private async Task ReceiveLoopAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side)
    {
        var buffer = new byte[1024 * 4]; // 4KB

        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();

            do
            {
                result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(ms.ToArray());
                await ProcessMessageAsync(webSocket, session, side, message);
            }
        }
    }

    /// <summary>
    /// 수신된 메시지를 파싱하고 처리합니다.
    /// </summary>
    private async Task ProcessMessageAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side,
        string rawMessage)
    {
        try
        {
            var clientMessage = JsonSerializer.Deserialize<ClientMessage>(
                rawMessage, JsonOptions.Default);

            if (clientMessage == null)
            {
                await SendErrorAsync(webSocket, "Invalid message format");
                return;
            }

            switch (clientMessage.Type)
            {
                case ClientMessageTypes.MoveUnits:
                    await HandleMoveUnitsAsync(webSocket, session, side, clientMessage.Payload);
                    break;

                case ClientMessageTypes.EncounterDecision:
                    await HandleEncounterDecisionAsync(webSocket, session, side, clientMessage.Payload);
                    break;

                case ClientMessageTypes.GetState:
                    await HandleGetStateAsync(webSocket, session, side);
                    break;

                case ClientMessageTypes.Ping:
                    await SendPongAsync(webSocket);
                    break;

                default:
                    await SendErrorAsync(webSocket, $"Unknown message type: {clientMessage.Type}");
                    break;
            }
        }
        catch (JsonException)
        {
            await SendErrorAsync(webSocket, "Invalid JSON");
        }
        catch (Exception ex)
        {
            await SendErrorAsync(webSocket, $"Internal error: {ex.Message}");
        }
    }

    // ========================================================================
    // 메시지 핸들러
    // ========================================================================

    private async Task HandleMoveUnitsAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side,
        JsonElement payload)
    {
        var movePayload = JsonSerializer.Deserialize<MoveUnitsPayload>(
            payload.GetRawText(), JsonOptions.Default);

        if (movePayload == null)
        {
            await SendErrorAsync(webSocket, "Invalid move_units payload");
            return;
        }

        var command = new MoveCommand(
            new NodeId(movePayload.From),
            new NodeId(movePayload.To),
            movePayload.Count);

        var result = await session.HandleMoveUnitsAsync(side, command);

        if (!result.IsSuccess)
        {
            await SendErrorAsync(webSocket, result.ErrorMessage ?? "Move failed", result.ErrorCode);
        }
    }

    private async Task HandleEncounterDecisionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side,
        JsonElement payload)
    {
        var encPayload = JsonSerializer.Deserialize<EncounterDecisionPayload>(
            payload.GetRawText(), JsonOptions.Default);

        if (encPayload == null)
        {
            await SendErrorAsync(webSocket, "Invalid encounter_decision payload");
            return;
        }

        var edgeId = new EdgeId(
            new NodeId(encPayload.FromNode),
            new NodeId(encPayload.ToNode));

        if (!Enum.TryParse<EncounterDecision>(encPayload.Decision, true, out var decision))
        {
            await SendErrorAsync(webSocket, $"Invalid decision: {encPayload.Decision}");
            return;
        }

        var result = await session.HandleEncounterDecisionAsync(side, edgeId, decision);

        if (!result.IsSuccess)
        {
            await SendErrorAsync(webSocket, result.ErrorMessage ?? "Decision failed", result.ErrorCode);
        }
    }

    private async Task HandleGetStateAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side)
    {
        var stateView = session.GetGameStateForPlayer(side);

        var serverMessage = new ServerMessage
        {
            Type = ServerMessageTypes.StateUpdate,
            EventType = "GameState",
            Payload = stateView,
            Timestamp = DateTime.UtcNow,
            Round = session.CurrentRound
        };

        var json = JsonSerializer.Serialize(serverMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, json);
    }

    // ========================================================================
    // 응답 헬퍼
    // ========================================================================

    private async Task SendTextAsync(System.Net.WebSockets.WebSocket webSocket, string message)
    {
        if (webSocket.State != WebSocketState.Open) return;

        var buffer = Encoding.UTF8.GetBytes(message);
        await webSocket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private async Task SendErrorAsync(System.Net.WebSockets.WebSocket webSocket, string message, string? code = null)
    {
        var errorMessage = new ServerMessage
        {
            Type = ServerMessageTypes.Error,
            EventType = "Error",
            Payload = new { message, code },
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(errorMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, json);
    }

    private async Task SendPongAsync(System.Net.WebSockets.WebSocket webSocket)
    {
        var pongMessage = new ServerMessage
        {
            Type = ServerMessageTypes.Pong,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(pongMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, json);
    }
}