// src/HexWar.Application/Sessions/GameSessionEvents.cs
namespace HexWar.Application.Sessions;

using HexWar.Domain.Enums;
using HexWar.Domain.Events;

public class GameOverEventArgs : EventArgs
{
    public string RoomId { get; }
    public PlayerSide? Winner { get; }
    public GameOverReason Reason { get; }

    public GameOverEventArgs(string roomId, PlayerSide? winner, GameOverReason reason)
    {
        RoomId = roomId;
        Winner = winner;
        Reason = reason;
    }
}

public class RoundResolvedEventArgs : EventArgs
{
    public string RoomId { get; }
    public int CompletedRound { get; }

    public RoundResolvedEventArgs(string roomId, int completedRound)
    {
        RoomId = roomId;
        CompletedRound = completedRound;
    }
}