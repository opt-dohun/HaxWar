namespace HexWar.Matchmaking.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HexWar.Application.Services;
using HexWar.Domain.Entities;
using HexWar.Domain.ValueObjects;
using StackExchange.Redis;

public class MatchmakingQueue
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IDistributedLock? _distributedLock;
    private readonly IGameRoomRepository? _repository;

    // Standalone fallback
    private readonly ConcurrentQueue<QueuedPlayer> _inMemoryQueue = new();
    private readonly object _matchLock = new();

    // 매칭 이벤트 핸들러
    public event EventHandler<MatchFoundEventArgs>? OnMatchFound;

    public MatchmakingQueue(
        IConnectionMultiplexer? redis = null,
        IDistributedLock? distributedLock = null,
        IGameRoomRepository? repository = null)
    {
        _redis = redis;
        _distributedLock = distributedLock;
        _repository = repository;
    }

    public async Task<QueuedPlayer> EnqueueAsync(string playerId, int rating, CancellationToken cancellationToken)
    {
        var player = new QueuedPlayer
        {
            PlayerId = playerId,
            Rating = rating,
            JoinedAt = DateTime.UtcNow,
            CancellationToken = cancellationToken
        };

        if (_redis != null)
        {
            var db = _redis.GetDatabase();

            var dto = new QueuedPlayerDto
            {
                PlayerId = player.PlayerId,
                Rating = player.Rating,
                JoinedAt = player.JoinedAt
            };
            var json = JsonSerializer.Serialize(dto);

            // Store player details in Redis hash
            await db.HashSetAsync("matchmaking:player_details", playerId, json);

            // Push to Redis queue (remove duplicate first if any)
            await db.ListRemoveAsync("matchmaking:queue", playerId);
            await db.ListRightPushAsync("matchmaking:queue", playerId);

            // Trigger matching asynchronously
            _ = Task.Run(async () => await TryMatchAsync());
        }
        else
        {
            _inMemoryQueue.Enqueue(player);
            TryMatchInMemory();
        }

        return player;
    }

    public async Task<bool> DequeueAsync(string playerId)
    {
        if (_redis != null)
        {
            var db = _redis.GetDatabase();
            await db.HashDeleteAsync("matchmaking:player_details", playerId);
            var removed = await db.ListRemoveAsync("matchmaking:queue", playerId);
            return removed > 0;
        }
        else
        {
            // Standalone Dequeue
            var remaining = _inMemoryQueue.Where(p => p.PlayerId != playerId).ToList();

            while (_inMemoryQueue.TryDequeue(out _)) { }

            foreach (var player in remaining)
            {
                _inMemoryQueue.Enqueue(player);
            }

            return remaining.Count < _inMemoryQueue.Count + 1;
        }
    }

    private async Task TryMatchAsync()
    {
        if (_redis == null || _distributedLock == null || _repository == null)
            return;

        // Acquire distributed lock for matching process
        using var lockHandle = await _distributedLock.TryAcquireAsync("matchmaking:queue", TimeSpan.FromSeconds(5));
        if (lockHandle == null)
        {
            return; // Busy, another server is matching
        }

        var db = _redis.GetDatabase();
        var queueLength = await db.ListLengthAsync("matchmaking:queue");
        if (queueLength < 2)
            return;

        // Pop two players
        var p1Val = await db.ListLeftPopAsync("matchmaking:queue");
        var p2Val = await db.ListLeftPopAsync("matchmaking:queue");

        if (p1Val.IsNullOrEmpty || p2Val.IsNullOrEmpty)
        {
            // Re-enqueue if we only got one
            if (!p1Val.IsNullOrEmpty) await db.ListLeftPushAsync("matchmaking:queue", p1Val);
            if (!p2Val.IsNullOrEmpty) await db.ListLeftPushAsync("matchmaking:queue", p2Val);
            return;
        }

        var p1Id = p1Val.ToString();
        var p2Id = p2Val.ToString();

        var p1Json = await db.HashGetAsync("matchmaking:player_details", p1Id);
        var p2Json = await db.HashGetAsync("matchmaking:player_details", p2Id);

        if (p1Json.IsNullOrEmpty || p2Json.IsNullOrEmpty)
        {
            // Stale cleanup
            if (!p1Json.IsNullOrEmpty)
            {
                await db.ListLeftPushAsync("matchmaking:queue", p1Val);
            }
            else
            {
                await db.HashDeleteAsync("matchmaking:player_details", p1Id);
            }

            if (!p2Json.IsNullOrEmpty)
            {
                await db.ListLeftPushAsync("matchmaking:queue", p2Val);
            }
            else
            {
                await db.HashDeleteAsync("matchmaking:player_details", p2Id);
            }

            // Retry matching
            _ = Task.Run(async () => await TryMatchAsync());
            return;
        }

        await db.HashDeleteAsync("matchmaking:player_details", p1Id);
        await db.HashDeleteAsync("matchmaking:player_details", p2Id);

        var p1Dto = JsonSerializer.Deserialize<QueuedPlayerDto>(p1Json.ToString());
        var p2Dto = JsonSerializer.Deserialize<QueuedPlayerDto>(p2Json.ToString());

        if (p1Dto != null && p2Dto != null)
        {
            var player1 = new QueuedPlayer
            {
                PlayerId = p1Dto.PlayerId,
                Rating = p1Dto.Rating,
                JoinedAt = p1Dto.JoinedAt,
                CancellationToken = CancellationToken.None
            };
            var player2 = new QueuedPlayer
            {
                PlayerId = p2Dto.PlayerId,
                Rating = p2Dto.Rating,
                JoinedAt = p2Dto.JoinedAt,
                CancellationToken = CancellationToken.None
            };

            OnMatchFound?.Invoke(this, new MatchFoundEventArgs(player1, player2));
        }

        // Recursively try to match remaining
        _ = Task.Run(async () => await TryMatchAsync());
    }

    private void TryMatchInMemory()
    {
        lock (_matchLock)
        {
            if (_inMemoryQueue.Count < 2) return;

            if (_inMemoryQueue.TryDequeue(out var player1) && _inMemoryQueue.TryDequeue(out var player2))
            {
                if (player1.CancellationToken.IsCancellationRequested)
                {
                    if (!player2.CancellationToken.IsCancellationRequested)
                        _inMemoryQueue.Enqueue(player2);
                    TryMatchInMemory();
                    return;
                }

                if (player2.CancellationToken.IsCancellationRequested)
                {
                    _inMemoryQueue.Enqueue(player1);
                    TryMatchInMemory();
                    return;
                }

                OnMatchFound?.Invoke(this, new MatchFoundEventArgs(player1, player2));
            }
        }
    }

    public async Task<QueueStatusInfo> GetStatusAsync(string playerId)
    {
        if (_redis != null)
        {
            var db = _redis.GetDatabase();
            var players = await db.ListRangeAsync("matchmaking:queue");
            var playerList = players.Select(p => p.ToString()).ToList();
            var index = playerList.IndexOf(playerId);

            return new QueueStatusInfo
            {
                IsInQueue = index >= 0,
                Position = index >= 0 ? index + 1 : 0,
                TotalInQueue = playerList.Count,
                EstimatedWaitSeconds = playerList.Count * 5
            };
        }
        else
        {
            var players = _inMemoryQueue.ToList();
            var player = players.FirstOrDefault(p => p.PlayerId == playerId);

            return new QueueStatusInfo
            {
                IsInQueue = player != null,
                Position = player != null ? players.IndexOf(player) + 1 : 0,
                TotalInQueue = players.Count,
                EstimatedWaitSeconds = players.Count * 5
            };
        }
    }
}

public class QueuedPlayerDto
{
    public string PlayerId { get; set; } = string.Empty;
    public int Rating { get; set; }
    public DateTime JoinedAt { get; set; }
}

public class QueuedPlayer
{
    public string PlayerId { get; init; } = string.Empty;
    public int Rating { get; init; }
    public DateTime JoinedAt { get; init; }
    
    [JsonIgnore]
    public CancellationToken CancellationToken { get; init; }
}

public class MatchFoundEventArgs : EventArgs
{
    public QueuedPlayer Player1 { get; }
    public QueuedPlayer Player2 { get; }

    public MatchFoundEventArgs(QueuedPlayer player1, QueuedPlayer player2)
    {
        Player1 = player1;
        Player2 = player2;
    }
}

public class QueueStatusInfo
{
    public bool IsInQueue { get; init; }
    public int Position { get; init; }
    public int TotalInQueue { get; init; }
    public int EstimatedWaitSeconds { get; init; }
}