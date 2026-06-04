namespace HexWar.Domain.Enums;

public enum GamePhase
{
    WatingForPlayers,
    Planning, // 라운드 시작 전 계획 단계
    Resolution, // 명령 실행 및 판정 진행
    GameOver,
}

/*
[WaitingForPlayers] → [Planning] ⇄ [Resolution] → [GameOver]
     ↑                    ↑           ↑               ↑
   플레이어 모집       이동명령     라운드해소      종료조건 달성
                         ↓
                   [Encounter]
                   조우 발생 시
                   결정 대기 상태
*/

