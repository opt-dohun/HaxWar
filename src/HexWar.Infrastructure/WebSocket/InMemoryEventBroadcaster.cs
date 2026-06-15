namespace HexWar.Infrastructure.WebSocket;

using System.Text.Json;
using HexWar.Application.Services;
using HexWar.Domain.Enums;
using HexWar.Domain.Events;

public class InMemoryEventBroadcaster : IEventBroadcaster
{
    private readonly ConnectionManager _connectionManager;
    private readonly Func<string, int> _getCurrentRound;

    /// <summary>
    /// 생성자
    /// </summary>
    /// <param name="connectionManager">WebSocket 연결 관리자</param>
    /// <param name="getCurrentRound">roomId로 현재 라운드를 조회하는 함수</param>
    public InMemoryEventBroadcaster(
        ConnectionManager connectionManager,
        Func<string, int> getCurrentRound)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _getCurrentRound = getCurrentRound ?? throw new ArgumentNullException(nameof(getCurrentRound));
    }

    /// <inheritdoc />
    public async Task BroadcastToRoomAsync(string roomId, IDomainEvent domainEvent, long sequenceNumber = 0)
    {
        var serverMessage = ServerMessage.FromDomainEvent(
            domainEvent, roomId, _getCurrentRound(roomId), sequenceNumber);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
        await _connectionManager.BroadcastToRoomAsync(roomId, bytes);
    }

    /// <inheritdoc />
    public async Task BroadcastToRoomAsync<T>(string roomId, T message, long sequenceNumber = 0) where T : class
    {
        var eventType = typeof(T).Name;
        var serverMessage = ServerMessage.FromDto(message, eventType, _getCurrentRound(roomId), sequenceNumber);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
        await _connectionManager.BroadcastToRoomAsync(roomId, bytes);
    }

    /// <inheritdoc />
    public async Task SendToPlayerAsync(string roomId, PlayerSide side, IDomainEvent domainEvent, long sequenceNumber = 0)
    {
        var serverMessage = ServerMessage.FromDomainEvent(
            domainEvent, roomId, _getCurrentRound(roomId), sequenceNumber);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
        await _connectionManager.SendToPlayerAsync(roomId, side.ToString(), bytes);
    }

    /// <inheritdoc />
    public async Task SendToPlayerAsync<T>(string roomId, PlayerSide side, T message, long sequenceNumber = 0) where T : class
    {
        var eventType = typeof(T).Name;
        var serverMessage = ServerMessage.FromDto(message, eventType, _getCurrentRound(roomId), sequenceNumber);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(serverMessage, JsonOptions.Default);
        await _connectionManager.SendToPlayerAsync(roomId, side.ToString(), bytes);
    }
}