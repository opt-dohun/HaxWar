namespace HexWar.Application.Sessions;

using System.Collections.Concurrent;
using HexWar.Application.Services;
using HexWar.Domain.Entities;

// 모든 활성 게임 세션을 관리
public class SessionRegistry
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly IGameRoomRepository _repository;
    private readonly IEventBroadcaster _eventBroadcaster;

    public SessionRegistry(IGameRoomRepository repository, IEventBroadcaster eventBroadcaster)
    {
        _repository = repository;
        _eventBroadcaster = eventBroadcaster;
    }

    // 새로운 게임 세션 생성
    public async Task<GameSession> CreateSessionAsync(string roomId)
    {
        if (_sessions.ContainsKey(roomId))
            throw new InvalidOperationException($"Session {roomId} already exists");

        var gameRoom = new GameRoom(roomId);
        gameRoom.InitializeMap();

        var session = new GameSession(gameRoom, _eventBroadcaster, _repository);

        // 종료 시 등록할 이벤트 핸들러 - 게임 종료 후 1분 뒤 메모리에서 제거 
        session.OnGameOver += async (s, e) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            _sessions.TryRemove(roomId, out _);
        };

        _sessions[roomId] = session;
        await _repository.SaveAsync(gameRoom);

        return session;
    }

    // 세션 조회
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