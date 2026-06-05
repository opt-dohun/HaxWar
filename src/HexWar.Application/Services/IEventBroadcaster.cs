namespace HexWar.Application.Services;

using HexWar.Domain.Enums;
using HexWar.Domain.Events;

public interface IEventBroadcaster
{
    Task BroadcastToRoomAsync(string roomId, IDomainEvent domainEvent);

    Task BroadcastToRoomAsync<T>(string roomId, T message) where T : class;

    Task SendToPlayerAsync(string roomId, PlayerSide side, IDomainEvent domainEvent);

    Task SendToPlayerAsync<T>(string roomId, PlayerSide side, T message) where T : class;
}