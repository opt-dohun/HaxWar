namespace HexWar.Infrastructure.Persistence;

using System.Collections.Concurrent;
using HexWar.Application.Services;
using HexWar.Domain.Entities;

/// <summary>
/// GameRoom의 인메모리 저장소 구현체
/// </summary>
public class InMemoryGameRoomRepository : IGameRoomRepository
{
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();

    public Task<GameRoom?> GetByIdAsync(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return Task.FromResult(room);
    }

    public Task SaveAsync(GameRoom gameRoom)
    {
        _rooms[gameRoom.RoomId] = gameRoom;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string roomId)
    {
        return Task.FromResult(_rooms.ContainsKey(roomId));
    }
}