namespace HexWar.Application.Sessions;

using HexWar.Domain.Enums;

public class PlayerSessionState
{
    public PlayerSide Side { get; }
    public bool IsConnected { get; set; }
    public DateTime LastActivityAt { get; set; }
    public bool MoveCommandCompleted { get; set; }

    // 조우 결정 완료 여부
    public bool EncounterDecisionsCompleted { get; set; }

    // 
    public bool IsPlanningComplete => MoveCommandCompleted && EncounterDecisionsCompleted;

    // 세션 접속 상태 초기값 생성
    public PlayerSessionState(PlayerSide side)
    {
        Side = side;
        IsConnected = true;
        LastActivityAt = DateTime.UtcNow;
    }

    public void ResetForNewRound()
    {
        MoveCommandCompleted = false;
        EncounterDecisionsCompleted = false;
        LastActivityAt = DateTime.UtcNow;
    }

    public void MarkActivity()
    {
        LastActivityAt = DateTime.UtcNow;
    }
}