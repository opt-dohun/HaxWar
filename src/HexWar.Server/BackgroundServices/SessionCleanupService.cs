namespace HexWar.Server.BackgroundServices;

using HexWar.Application.Sessions;
using HexWar.Domain.Enums;
using HexWar.Infrastructure.WebSocket;

public class SessionCleanupService : BackgroundService
{
    private readonly SessionRegistry _sessionRegistry;
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger<SessionCleanupService> _logger;

    // TimeSpan 자료형 이란 ? 특정 시간의 간격을 나타내는 값
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(3);
    private readonly TimeSpan _inactiveThreshold = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _gameOverRetention = TimeSpan.FromMinutes(5);

    public SessionCleanupService(
        SessionRegistry sessionRegistry,
        ConnectionManager connectionManager,
        ILogger<SessionCleanupService> logger)
    {
        _sessionRegistry = sessionRegistry;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        _logger.LogInformation("Session cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {

                var staleCleaned = _connectionManager.CleanupStaleConnections();
                if (staleCleaned > 0)
                {
                    _logger.LogInformation("Cleaned {Count} stale connections", staleCleaned);
                }

                await CleanupAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);

        }
    }

    private async Task CleanupAsync()
    {
        // 활성화된 모든 세션 조회
        var sessions = _sessionRegistry.GetActiveSessions();
        var now = DateTime.UtcNow;
        var cleanupCount = 0;

        foreach (var session in sessions)
        {
            if (ShouldCleanup(session, now))
            {
                _logger.LogInformation("Cleaning session {RoomId}: Phase={Phase}, Connected={Connected}, Inactive={InactiveTime}", session.RoomId, session.CurrentPhase, session.ConnectedPlayerCount, session.LastActivityElapsed);

                await CleanupConnectionsAsync(session.RoomId);

                _sessionRegistry.RemoveSession(session.RoomId);


                // 명시적 리소스 해제 (Dispose 호출)
                // session.Dispose() : IDisposable 인터페이스를 구현한 객체가 사용하던 메모리 및 리소스를 시스템에 반환
                session.Dispose();
                cleanupCount++;
            }
        }

        if (cleanupCount > 0)
        {
            _logger.LogInformation(
                "Cleaned {Count} sessions. Active connections: {Connections}",
                cleanupCount, _connectionManager.GetTotalConnectionCount());
        }
    }

    private async Task CleanupConnectionsAsync(string roomId)
    {
        // 방에 연결된 플레이어들에게 게임 종료 알림 전송
        var disconnectMessageBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "game_event",
            event_type = "SessionClosed",
            payload = new { roomId, reason = "session_cleaned" },
            timestamp = DateTime.UtcNow
        }, JsonOptions.Default);

        await _connectionManager.BroadcastToRoomAsync(roomId, disconnectMessageBytes);

        // 약간의 지연 후 연결 종료 (메시지 전송 시간 확보)
        await Task.Delay(500);

        // 모든 연결 닫기 및 제거
        await _connectionManager.CleanupRoomAsync(roomId);
    }

    // 게임 세션 종료 시기 결정
    private bool ShouldCleanup(GameSession session, DateTime now)
    {
        // 1. 게임 종료 + 유예 시간 경과
        if (session.CurrentPhase == GamePhase.GameOver)
        {
            return session.LastActivityElapsed > _gameOverRetention;
        }

        // 2. 플레이어 없음 (둘 다 연결 끊김)
        if (session.ConnectedPlayerCount == 0 && session.LastActivityElapsed > _inactiveThreshold)
        {
            return true;
        }

        // 3. 장기간 비활성
        if (session.ConnectedPlayerCount == 1 && session.LastActivityElapsed > _inactiveThreshold * 2)
        {
            return true;
        }

        // 4. 비정상 상태 (WaitingForPlayers에서 5분 이상)
        if (session.CurrentPhase == GamePhase.WatingForPlayers && session.LastActivityElapsed > TimeSpan.FromMinutes(5))
        {
            return true;
        }

        return false;
    }


}

/*
SessionCleanupService.CleanupAsync()
    │
    ├── 1. ShouldCleanup(session) → true
    │       (게임 종료 후 5분 경과 등)
    │
    ├── 2. CleanupConnectionsAsync(roomId)
    │       ├── BroadcastToRoom(roomId, "SessionClosed")
    │       │   → 클라이언트에게 "서버가 세션을 닫습니다" 알림
    │       │
    │       ├── Task.Delay(500ms)
    │       │   → 메시지 전송 완료 대기
    │       │
    │       └── ConnectionManager.CleanupRoomAsync(roomId)
    │           ├── 각 소켓에 CloseAsync(NormalClosure)
    │           ├── 3초 타임아웃 대기
    │           └── 남은 연결 강제 제거
    │
    ├── 3. SessionRegistry.RemoveSession(roomId)
    │       └── _sessions.TryRemove(roomId)
    │
    └── 4. session.Dispose()
            ├── StopPlanningTimer()
            ├── _eventBuffer.Clear()
            └── _lock.Dispose()
*/