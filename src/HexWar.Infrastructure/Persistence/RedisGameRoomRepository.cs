// src/HexWar.Infrastructure/Persistence/RedisGameRoomRepository.cs
namespace HexWar.Infrastructure.Persistence;

using System.Text.Json;
using HexWar.Application.Services;
using HexWar.Domain.Entities;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// Redis 기반 GameRoom 저장소.
/// 서버 재시작, 다중 인스턴스 환경에서 게임 상태를 공유합니다.
/// </summary>
public class RedisGameRoomRepository : IGameRoomRepository, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ISubscriber _subscriber;
    private readonly RedisConfiguration _config;
    private readonly ILogger<RedisGameRoomRepository> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public RedisGameRoomRepository(
        RedisConfiguration config,
        ILogger<RedisGameRoomRepository> logger)
    {
        _config = config;
        _logger = logger;

        try
        {
            _redis = ConnectionMultiplexer.Connect(config.ToConfigurationOptions());
            _db = _redis.GetDatabase();
            _subscriber = _redis.GetSubscriber();

            _logger.LogInformation(
                "Redis connected: {Endpoint}, DB: {Db}",
                config.ConnectionString, config.Database);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Redis at {Endpoint}", config.ConnectionString);
            throw;
        }
    }

    // ============================================================
    // IGameRoomRepository 구현
    // ============================================================

    public async Task<GameRoom?> GetByIdAsync(string roomId)
    {
        try
        {
            var key = GetRoomKey(roomId);
            var value = await _db.StringGetAsync(key);

            if (value.IsNullOrEmpty) return null;

            return JsonSerializer.Deserialize<GameRoom>(value!, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GameRoom {RoomId} from Redis", roomId);
            return null;
        }
    }

    public async Task SaveAsync(GameRoom gameRoom)
    {
        try
        {
            var key = GetRoomKey(gameRoom.RoomId);
            var json = JsonSerializer.Serialize(gameRoom, JsonOptions);

            var expiry = gameRoom.Phase == Domain.Enums.GamePhase.GameOver
                ? TimeSpan.FromMinutes(_config.GameOverExpiryMinutes)
                : TimeSpan.FromMinutes(_config.GameSessionExpiryMinutes);

            await _db.StringSetAsync(key, json, expiry);

            // 메타데이터 별도 저장 (빠른 조회용)
            await _db.HashSetAsync(GetRoomMetaKey(gameRoom.RoomId), new HashEntry[]
            {
                new("phase", gameRoom.Phase.ToString()),
                new("round", gameRoom.CurrentRound),
                new("updated_at", DateTime.UtcNow.ToString("O"))
            });
            await _db.KeyExpireAsync(GetRoomMetaKey(gameRoom.RoomId), expiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save GameRoom {RoomId} to Redis", gameRoom.RoomId);
        }
    }

    public async Task<bool> ExistsAsync(string roomId)
    {
        try
        {
            return await _db.KeyExistsAsync(GetRoomKey(roomId));
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // Redis 전용 확장 기능
    // ============================================================

    /// <summary>
    /// 플레이어 세션 정보 저장
    /// </summary>
    public async Task SavePlayerSessionAsync(string playerId, string roomId, string playerSide)
    {
        var sessionInfo = new PlayerSessionInfo
        {
            RoomId = roomId,
            PlayerSide = playerSide,
            ServerId = Environment.MachineName,
            ConnectedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(sessionInfo, JsonOptions);
        await _db.StringSetAsync(
            $"player:{playerId}",
            json,
            TimeSpan.FromMinutes(30)); // 30분 TTL
    }

    /// <summary>
    /// 플레이어가 속한 게임 찾기
    /// </summary>
    public async Task<string?> FindRoomByPlayerAsync(string playerId)
    {
        var value = await _db.StringGetAsync($"player:{playerId}");
        if (value.IsNullOrEmpty) return null;

        var session = JsonSerializer.Deserialize<PlayerSessionInfo>(value!, JsonOptions);
        return session?.RoomId;
    }

    /// <summary>
    /// 활성 게임방 목록 조회
    /// </summary>
    public async Task<List<string>> GetActiveRoomIdsAsync(int limit = 100)
    {
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        var keys = new List<string>();

        await foreach (var key in server.KeysAsync(pattern: "gameroom:*"))
        {
            keys.Add(key.ToString().Replace("gameroom:", ""));
            if (keys.Count >= limit) break;
        }

        return keys;
    }

    /// <summary>
    /// Pub/Sub 이벤트 발행 (분산 서버 간 통신)
    /// </summary>
    public async Task PublishGameEventAsync(string roomId, string eventJson)
    {
        await _subscriber.PublishAsync(
            new RedisChannel($"game_events:{roomId}", RedisChannel.PatternMode.Literal),
            eventJson);
    }

    /// <summary>
    /// Pub/Sub 이벤트 구독
    /// </summary>
    public void SubscribeToGameEvents(string roomId, Action<string> handler)
    {
        _subscriber.Subscribe(
            new RedisChannel($"game_events:{roomId}", RedisChannel.PatternMode.Literal),
            (channel, message) => handler(message!));
    }

    // ============================================================
    // 헬퍼
    // ============================================================

    private static string GetRoomKey(string roomId) => $"gameroom:{roomId}";
    private static string GetRoomMetaKey(string roomId) => $"gameroom:{roomId}:meta";

    public void Dispose()
    {
        _redis?.Dispose();
    }
}

/// <summary>
/// Redis에 저장되는 플레이어 세션 정보
/// </summary>
public class PlayerSessionInfo
{
    public string RoomId { get; set; } = string.Empty;
    public string PlayerSide { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
}