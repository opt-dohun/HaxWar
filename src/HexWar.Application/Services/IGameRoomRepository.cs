namespace HexWar.Application.Services;

using HexWar.Domain.Entities;

public interface IGameRoomRepository
{
    Task<GameRoom?> GetByIdAsync(string roomId);
    Task SaveAsync(GameRoom gameRoom);
    Task<bool> ExistsAsync(string roomId);
}