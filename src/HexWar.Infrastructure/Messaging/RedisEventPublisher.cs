namespace HexWar.Infrastructure.Messaging;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using HexWar.Application.Messaging;
using HexWar.Application.Services;
using HexWar.Domain.Events;

/// <summary>
/// Redis Pub/Sub을 통한 분산 이벤트 발행/구독 구현체
/// 
/// 책임:
/// 1. 이 서버에서 발생한 이벤트를 Redis 채널에 발행
/// 2. 다른 서버에서 발행한 이벤트를 Redis 채널에서 수신
/// 
/// WebSocket 전송은 InMemoryEventBroadcaster가 담당 (분리된 책임)
/// </summary>
public class RedisEventPublisher : IGameEventPublisher, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly ILogger<RedisEventPublisher> _logger;

    // roomId → 구독 핸들러 매핑
    private readonly ConcurrentDictionary<string, Func<string, Task>> _subscriptions = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RedisEventPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisEventPublisher> logger)
    {
        _redis = redis;
        _subscriber = redis.GetSubscriber();
        _logger = logger;
    }

    // ============================================================
    // IGameEventPublisher 구현
    // ============================================================

    /// <summary>
    /// 이벤트를 Redis 채널에 발행합니다.
    /// </summary>
    public async Task PublishAsync(string roomId, IDomainEvent domainEvent, long sequenceNumber)
    {
        try
        {
            var channel = GetRoomChannel(roomId);
            
            var message = new DistributedEventMessage
            {
                RoomId = roomId,
                EventType = domainEvent.GetType().Name,
                EventData = JsonSerializer.SerializeToElement(domainEvent, JsonOptions),
                SequenceNumber = sequenceNumber,
                SourceServerId = ServerIdentity.Id,
                Timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(message, JsonOptions);
            await _subscriber.PublishAsync(channel, json);

            _logger.LogDebug(
                "Published event {EventType} to channel {Channel} (seq={Seq})",
                message.EventType, channel, sequenceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event to Redis for room {RoomId}", roomId);
        }
    }

    /// <summary>
    /// 특정 방의 이벤트를 구독합니다.
    /// </summary>
    public void Subscribe(string roomId, Func<string, Task> eventHandler)
    {
        var channel = GetRoomChannel(roomId);
        
        // 이미 구독 중이면 무시
        if (_subscriptions.ContainsKey(roomId)) return;

        _subscriptions[roomId] = eventHandler;

        _subscriber.Subscribe(channel, async (redisChannel, redisValue) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<DistributedEventMessage>(
                    redisValue.ToString(), JsonOptions);

                if (message == null) return;

                // 자기 서버에서 발행한 이벤트는 무시 (루프 방지)
                if (message.SourceServerId == ServerIdentity.Id)
                {
                    _logger.LogTrace("Ignoring self-published event for room {RoomId}", roomId);
                    return;
                }

                _logger.LogDebug(
                    "Received event {EventType} from {SourceServer} for room {RoomId}",
                    message.EventType, message.SourceServerId, roomId);

                // 등록된 핸들러 호출
                if (_subscriptions.TryGetValue(roomId, out var handler))
                {
                    await handler(redisValue.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Redis pub/sub message for room {RoomId}", roomId);
            }
        });

        _logger.LogInformation("Subscribed to channel {Channel}", channel);
    }

    /// <summary>
    /// 구독 해제
    /// </summary>
    public void Unsubscribe(string roomId)
    {
        var channel = GetRoomChannel(roomId);
        _subscriber.Unsubscribe(channel);
        _subscriptions.TryRemove(roomId, out _);
        
        _logger.LogInformation("Unsubscribed from channel {Channel}", channel);
    }

    // ============================================================
    // 헬퍼
    // ============================================================

    private static RedisChannel GetRoomChannel(string roomId)
    {
        return new RedisChannel(
            $"game_events:{roomId}",
            RedisChannel.PatternMode.Literal);
    }

    public void Dispose()
    {
        foreach (var roomId in _subscriptions.Keys.ToList())
        {
            Unsubscribe(roomId);
        }
    }
}
