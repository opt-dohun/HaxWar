namespace HexWar.Application.Services;

using HexWar.Domain.Enums;
using HexWar.Domain.Events;

public interface IEventBroadcaster
{
    Task BroadcastToRoomAsync(string roomId, IDomainEvent domainEvent, long sequenceNumber = 0);

    Task BroadcastToRoomAsync<T>(string roomId, T message, long sequenceNumber = 0) where T : class;

    Task SendToPlayerAsync(string roomId, PlayerSide side, IDomainEvent domainEvent, long sequenceNumber = 0);

    Task SendToPlayerAsync<T>(string roomId, PlayerSide side, T message, long sequenceNumber = 0) where T : class;
}