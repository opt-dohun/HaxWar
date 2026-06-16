// src/HexWar.Infrastructure/WebSocket/GameWebSocketHandler.cs
namespace HexWar.Infrastructure.WebSocket;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HexWar.Application.Sessions;
using HexWar.Domain.Commands;
using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// WebSocket 연결을 처리하고 GameSession으로 명령을 전달합니다.
/// </summary>
public class GameWebSocketHandler
{
    private readonly ConnectionManager _connectionManager;
    private readonly SessionRegistry _sessionRegistry;
    private readonly ILogger<GameWebSocketHandler> _logger;

    public GameWebSocketHandler(ConnectionManager connectionManager, SessionRegistry sessionRegistry, ILogger<GameWebSocketHandler> logger)
    {
        _connectionManager = connectionManager;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
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

        var session = await _sessionRegistry.GetOrCreateSessionAsync(roomId);
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
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 4); // 4KB

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var firstResult = await webSocket.ReceiveAsync(
                    buffer.AsMemory(), CancellationToken.None);

                if (firstResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }

                if (firstResult.MessageType == WebSocketMessageType.Text)
                {
                    if (firstResult.EndOfMessage)
                    {
                        // Fast path: fits in one receive buffer chunk. No MemoryStream allocated.
                        try
                        {
                            var clientMessage = JsonSerializer.Deserialize<ClientMessage>(
                                buffer.AsSpan(0, firstResult.Count), JsonOptions.Default);
                            if (clientMessage != null)
                            {
                                await ProcessMessageAsync(webSocket, session, side, clientMessage);
                            }
                            else
                            {
                                await SendErrorAsync(webSocket, "Invalid message format");
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
                    else
                    {
                        // Slow path: multi-packet message. Fallback to MemoryStream.
                        using var ms = new MemoryStream();
                        ms.Write(buffer, 0, firstResult.Count);

                        System.Net.WebSockets.ValueWebSocketReceiveResult loopResult;
                        do
                        {
                            loopResult = await webSocket.ReceiveAsync(
                                buffer.AsMemory(), CancellationToken.None);
                            ms.Write(buffer, 0, loopResult.Count);
                        }
                        while (!loopResult.EndOfMessage);

                        if (loopResult.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            break;
                        }

                        ms.Position = 0;
                        try
                        {
                            var clientMessage = await JsonSerializer.DeserializeAsync<ClientMessage>(ms, JsonOptions.Default);
                            if (clientMessage != null)
                            {
                                await ProcessMessageAsync(webSocket, session, side, clientMessage);
                            }
                            else
                            {
                                await SendErrorAsync(webSocket, "Invalid message format");
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
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("WebSocket connection closed for Room={RoomId}, Player={Side}: {Message}", session.RoomId, side, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket connection cancelled for Room={RoomId}, Player={Side}", session.RoomId, side);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket receive loop for Room={RoomId}, Player={Side}", session.RoomId, side);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 수신된 메시지를 파싱하고 처리합니다.
    /// </summary>
    private async Task ProcessMessageAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side,
        ClientMessage clientMessage)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[WS] Received from Room={RoomId}, Player={Side}: Type={Type}", session.RoomId, side, clientMessage.Type);
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

            case ClientMessageTypes.ReconnectSync:
                await HandleReconnectSyncAsync(webSocket, session, side, clientMessage.Payload);
                break;

            default:
                await SendErrorAsync(webSocket, $"Unknown message type: {clientMessage.Type}");
                break;
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
        else
        {
            await BroadcastStateAsync(session);
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
        else
        {
            await BroadcastStateAsync(session);
        }
    }

    private async Task HandleGetStateAsync(
        System.Net.WebSockets.WebSocket webSocket,
        GameSession session,
        PlayerSide side)
    {
        var stateView = await session.GetGameStateForPlayerAsync(side);

        var serverMessage = new ServerMessage
        {
            Type = ServerMessageTypes.StateUpdate,
            EventType = "GameState",
            Payload = stateView,
            Timestamp = DateTime.UtcNow,
            Round = session.CurrentRound
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, bytes);
    }

    private async Task BroadcastStateAsync(GameSession session)
    {
        foreach (var side in new[] { PlayerSide.A, PlayerSide.B })
        {
            var socket = _connectionManager.GetConnection(session.RoomId, side.ToString());
            if (socket != null && socket.State == WebSocketState.Open)
            {
                var stateView = await session.GetGameStateForPlayerAsync(side);
                var serverMessage = new ServerMessage
                {
                    Type = ServerMessageTypes.StateUpdate,
                    EventType = "GameState",
                    Payload = stateView,
                    Timestamp = DateTime.UtcNow,
                    Round = session.CurrentRound
                };

                var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
                await SendTextAsync(socket, bytes);
            }
        }
    }

    // ========================================================================
    // 응답 헬퍼
    // ========================================================================

    private async Task SendTextAsync(System.Net.WebSockets.WebSocket webSocket, ReadOnlyMemory<byte> messageBytes)
    {
        if (webSocket.State != WebSocketState.Open) return;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[WS] Sending: {Message}", Encoding.UTF8.GetString(messageBytes.Span));
        }

        await webSocket.SendAsync(
            messageBytes,
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

        var bytes = JsonSerializer.SerializeToUtf8Bytes(errorMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, bytes);
    }

    private async Task SendPongAsync(System.Net.WebSockets.WebSocket webSocket)
    {
        var pongMessage = new ServerMessage
        {
            Type = ServerMessageTypes.Pong,
            Timestamp = DateTime.UtcNow
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(pongMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, bytes);
    }

    private async Task HandleReconnectSyncAsync(
        WebSocket webSocket, GameSession session, PlayerSide side, JsonElement payload)
    {
        var syncPayload = JsonSerializer.Deserialize<ReconnectSyncPayload>(
            payload.GetRawText(), JsonOptions.Default);

        if (syncPayload == null) return;

        // 누락된 이벤트 전송
        var missedEvents = session.GetEventsAfter(syncPayload.LastSeenSequence);

        foreach (var buffered in missedEvents)
        {
            var message = new ServerMessage
            {
                Type = "game_event",
                EventType = buffered.Event.GetType().Name,
                Payload = buffered.Event,
                SequenceNumber = buffered.SequenceNumber,
                Timestamp = buffered.Timestamp
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions.Default);
            await SendTextAsync(webSocket, bytes);
        }

        // 현재 상태도 전송
        var stateView = await session.GetGameStateForPlayerAsync(side);
        var stateMessage = new ServerMessage
        {
            Type = "state_update",
            EventType = "GameState",
            Payload = stateView,
            Timestamp = DateTime.UtcNow,
            Round = session.CurrentRound
        };
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(stateMessage, JsonOptions.Default);
        await SendTextAsync(webSocket, stateBytes);
    }
}