namespace HexWar.Application.Sessions;

using System.Collections.Concurrent;
using HexWar.Application.Services;
using HexWar.Domain.Entities;
using Microsoft.Extensions.Logging;

// 모든 활성 게임 세션을 관리
public class SessionRegistry
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly IGameRoomRepository _repository;
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly IGameEventPublisher? _eventPublisher;
    private readonly IDistributedLock? _distributedLock;
    private readonly ILogger<GameSession>? _logger;

    public SessionRegistry(
        IGameRoomRepository repository,
        IEventBroadcaster eventBroadcaster,
        IGameEventPublisher? eventPublisher = null,
        IDistributedLock? distributedLock = null,
        ILogger<GameSession>? logger = null)
    {
        _repository = repository;
        _eventBroadcaster = eventBroadcaster;
        _eventPublisher = eventPublisher;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    // 새로운 게임 세션 생성
    public async Task<GameSession> CreateSessionAsync(string roomId)
    {
        if (_sessions.ContainsKey(roomId))
            throw new InvalidOperationException($"Session {roomId} already exists");

        var gameRoom = new GameRoom(roomId);
        gameRoom.InitializeMap();
        await _repository.SaveAsync(gameRoom);

        var session = new GameSession(
            roomId, 
            _eventBroadcaster, 
            _repository, 
            _eventPublisher, 
            _distributedLock, 
            _logger);

        // 종료 시 등록할 이벤트 핸들러 - 게임 종료 후 1분 뒤 메모리에서 제거 
        session.OnGameOver += async (s, e) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            _sessions.TryRemove(roomId, out _);
        };

        _sessions[roomId] = session;

        return session;
    }

    // 세션 조회 또는 Redis로부터 복원 (Hydrate)
    public async Task<GameSession?> GetOrCreateSessionAsync(string roomId)
    {
        if (_sessions.TryGetValue(roomId, out var session))
        {
            return session;
        }

        if (await _repository.ExistsAsync(roomId))
        {
            var hydratedSession = new GameSession(
                roomId, 
                _eventBroadcaster, 
                _repository, 
                _eventPublisher, 
                _distributedLock, 
                _logger);

            hydratedSession.OnGameOver += async (s, e) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                _sessions.TryRemove(roomId, out _);
            };

            _sessions[roomId] = hydratedSession;
            return hydratedSession;
        }

        return null;
    }

    // 세션 조회 (로컬 전용)
    public GameSession? GetSession(string roomId)
    {
        _sessions.TryGetValue(roomId, out var session);
        return session;
    }

    // 특정 플레이어가 속한 세션 찾기
    public GameSession? FindSessionByPlayer(string playerId)
    {
        return _sessions.Values.FirstOrDefault(s =>
        {
            var gameRoom = s.GetType().GetProperty("RoomId")?.GetValue(s) as string;
            // 실제 구현에서는 GameRoom.Players를 확인
            return false;
        });
    }

    // 모든 활성 세션 조회
    public IReadOnlyCollection<GameSession> GetActiveSessions()
    {
        return _sessions.Values.ToList().AsReadOnly();
    }

    // 세션 제거
    public void RemoveSession(string roomId)
    {
        if (_sessions.TryRemove(roomId, out var session))
        {
            session.Dispose();
        }
    }
}