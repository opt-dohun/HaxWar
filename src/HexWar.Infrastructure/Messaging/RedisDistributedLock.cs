// src/HexWar.Infrastructure/Messaging/RedisDistributedLock.cs
namespace HexWar.Infrastructure.Messaging;

using HexWar.Application.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// Redis 기반 분산 락 구현체
/// 
/// Redlock 알고리즘의 핵심 아이디어를 단일 Redis 인스턴스에 적용:
/// - SET NX PX로 원자적 락 획득
/// - Lua 스크립트로 안전한 락 해제 (소유자 확인)
/// - 만료 시간으로 데드락 방지
/// </summary>
public class RedisDistributedLock : IDistributedLock
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<RedisDistributedLock> _logger;

    // Lua 스크립트: 내가 소유한 락만 해제
    private const string UnlockScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end";

    public RedisDistributedLock(
        IConnectionMultiplexer redis,
        ILogger<RedisDistributedLock> logger)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string resourceKey, TimeSpan expiry, TimeSpan? acquireTimeout = null)
    {
        var lockKey = $"lock:{resourceKey}";
        var lockId = $"{ServerIdentity.Id}:{Guid.NewGuid():N}";
        var deadline = acquireTimeout.HasValue
            ? DateTime.UtcNow + acquireTimeout.Value
            : DateTime.UtcNow; // 단발 시도

        const int retryIntervalMs = 50;

        try
        {
            while (true)
            {
                var acquired = await _db.StringSetAsync(
                    lockKey,
                    lockId,
                    expiry,
                    When.NotExists,
                    CommandFlags.None);

                if (acquired)
                {
                    _logger.LogDebug("Lock acquired: {Key} by {LockId}", lockKey, lockId);
                    return new RedisLockHandle(_db, lockKey, lockId, _logger);
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining.TotalMilliseconds <= 0)
                {
                    _logger.LogDebug("Lock contention: {Key} is held by another server", lockKey);
                    return null;
                }

                var waitMs = (int)Math.Min(retryIntervalMs, remaining.TotalMilliseconds);
                await Task.Delay(waitMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire lock for {Key}", lockKey);
            return null;
        }
    }

    private class RedisLockHandle : IDistributedLockHandle
    {
        private readonly IDatabase _db;
        private readonly ILogger _logger;
        private bool _disposed; // 리소스 해제 여부 체크 

        public string ResourceKey { get; }
        public string LockId { get; }

        public RedisLockHandle(
            IDatabase db,
            string lockKey,
            string lockId,
            ILogger logger)
        {
            _db = db;
            ResourceKey = lockKey.Replace("lock:", "");
            LockId = lockId;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Lua 스크립트로 안전하게 해제 (내 락인지 확인)
                var result = _db.ScriptEvaluate(
                    UnlockScript,
                    new RedisKey[] { $"lock:{ResourceKey}" },
                    new RedisValue[] { LockId });

                if ((int)result == 1)
                {
                    _logger.LogDebug("Lock released: {Key}", ResourceKey);
                }
                else
                {
                    _logger.LogWarning(
                        "Lock already expired or owned by other: {Key}", ResourceKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing lock: {Key}", ResourceKey);
            }
        }
    }
}

// 기본적인 매커니즘
/*
[Player A → Server 1]           [Player B → Server 2]
        │                               │
        │ MoveUnits 요청                 │ MoveUnits 요청
        ▼                               ▼
Server 1: 분산 락 획득 시도       Server 2: 분산 락 획득 시도
        │                               │
        ├── 락 획득 성공!               ├── 락 획득 실패 (Server 1 소유)
        │   ├── Redis에서 GameRoom 로드  │   └── "Server busy" 응답
        │   ├── GameRoom.MoveUnits()    │
        │   ├── Redis에 GameRoom 저장   │
        │   ├── Pub/Sub 이벤트 발행     │
        │   └── 락 해제                 │
        │                               │
        └──→ Redis Pub/Sub ──────────────────→ Server 2 수신
                                                │
                                                └── WebSocket → Player B
*/