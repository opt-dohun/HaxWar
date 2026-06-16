namespace HexWar.Matchmaking.Services;

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using HexWar.Application.Sessions;
using HexWar.Matchmaking;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// gRPC 매치메이킹 서비스 구현체
/// </summary>
public class MatchmakingService : HexWar.Matchmaking.MatchmakingService.MatchmakingServiceBase
{
    private readonly MatchmakingQueue _queue;
    private readonly SessionRegistry _sessionRegistry;
    private readonly ILogger<MatchmakingService> _logger;
    private readonly ISubscriber? _sub;
    
    // 매칭 완료된 플레이어에게 결과를 전달하기 위한 채널과 비동기 완료 알림용 TaskCompletionSource
    private readonly ConcurrentDictionary<string, (IServerStreamWriter<MatchmakingUpdate> Stream, TaskCompletionSource<MatchmakingUpdate> Completion)> _waitingPlayers = new();

    public MatchmakingService(
        MatchmakingQueue queue,
        SessionRegistry sessionRegistry,
        ILogger<MatchmakingService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _queue = queue;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
        
        // 매칭 완료 이벤트 구독
        _queue.OnMatchFound += OnMatchFound;

        if (redis != null)
        {
            _sub = redis.GetSubscriber();
            _sub.Subscribe(RedisChannel.Literal("matchmaking:matches"), (channel, message) =>
            {
                HandleDistributedMatch(message.ToString());
            });
        }
    }

    private void HandleDistributedMatch(string messageJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<DistributedMatchPayload>(messageJson);
            if (payload == null) return;

            _logger.LogInformation("Received distributed match event: {RoomId}, Player1={P1}, Player2={P2}", 
                payload.RoomId, payload.Player1Id, payload.Player2Id);

            var wsEndpoint = $"ws://localhost:5183/ws/game/{payload.RoomId}";

            // Player 1에게 매칭 결과 전송
            if (_waitingPlayers.TryGetValue(payload.Player1Id, out var state1))
            {
                state1.Completion.TrySetResult(new MatchmakingUpdate
                {
                    Status = MatchmakingStatus.Matched,
                    MatchResult = new MatchFoundResult
                    {
                        RoomId = payload.RoomId,
                        PlayerSide = "A",
                        WsEndpoint = wsEndpoint,
                        OpponentId = payload.Player2Id
                    }
                });
            }

            // Player 2에게 매칭 결과 전송
            if (_waitingPlayers.TryGetValue(payload.Player2Id, out var state2))
            {
                state2.Completion.TrySetResult(new MatchmakingUpdate
                {
                    Status = MatchmakingStatus.Matched,
                    MatchResult = new MatchFoundResult
                    {
                        RoomId = payload.RoomId,
                        PlayerSide = "B",
                        WsEndpoint = wsEndpoint,
                        OpponentId = payload.Player1Id
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling distributed match message");
        }
    }

    /// <summary>
    /// 큐 참가 (서버 스트리밍)
    /// </summary>
    public override async Task JoinQueue(
        JoinQueueRequest request,
        IServerStreamWriter<MatchmakingUpdate> responseStream,
        ServerCallContext context)
    {
        var playerId = request.PlayerId;
        _logger.LogInformation("Player {PlayerId} joined matchmaking queue", playerId);

        // 이미 큐에 있는지 확인
        if (_waitingPlayers.ContainsKey(playerId))
        {
            await responseStream.WriteAsync(new MatchmakingUpdate
            {
                Status = MatchmakingStatus.Error
            });
            return;
        }

        // 응답 스트림 및 완료 알림 등록
        var matchCompletion = new TaskCompletionSource<MatchmakingUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitingPlayers[playerId] = (responseStream, matchCompletion);

        // 큐에 등록
        var queuedPlayer = await _queue.EnqueueAsync(playerId, request.Rating, context.CancellationToken);

        try
        {
            // 매칭될 때까지 상태 업데이트 전송
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (matchCompletion.Task.IsCompleted)
                {
                    break;
                }

                var status = await _queue.GetStatusAsync(playerId);
                
                if (status.IsInQueue)
                {
                    await responseStream.WriteAsync(new MatchmakingUpdate
                    {
                        Status = MatchmakingStatus.Searching,
                        QueuePosition = status.Position,
                        EstimatedWaitSeconds = status.EstimatedWaitSeconds
                    });
                }
                else
                {
                    // 큐에 없고 매칭도 완료되지 않았다면 매칭 처리 중이므로 잠시 대기
                    var completedTask = await Task.WhenAny(matchCompletion.Task, Task.Delay(3000, context.CancellationToken));
                    if (completedTask == matchCompletion.Task)
                    {
                        break;
                    }
                    else
                    {
                        // 3초 이내에 매칭 정보가 안 온다면 비정상 종료로 판단
                        break;
                    }
                }

                await Task.WhenAny(matchCompletion.Task, Task.Delay(1000, context.CancellationToken));
            }

            if (matchCompletion.Task.IsCompleted)
            {
                var finalUpdate = await matchCompletion.Task;
                await responseStream.WriteAsync(finalUpdate);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Player {PlayerId} cancelled matchmaking", playerId);
            await _queue.DequeueAsync(playerId);
            await responseStream.WriteAsync(new MatchmakingUpdate
            {
                Status = MatchmakingStatus.Cancelled
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during matchmaking for player {PlayerId}", playerId);
            await responseStream.WriteAsync(new MatchmakingUpdate
            {
                Status = MatchmakingStatus.Error
            });
        }
        finally
        {
            _waitingPlayers.TryRemove(playerId, out _);
            await _queue.DequeueAsync(playerId);
        }
    }

    /// <summary>
    /// 매칭 완료 처리
    /// </summary>
    private async void OnMatchFound(object? sender, MatchFoundEventArgs e)
    {
        var roomId = Guid.NewGuid().ToString("N")[..8]; // 8자리 짧은 ID
        
        try
        {
            // 게임 세션 생성
            var session = await _sessionRegistry.CreateSessionAsync(roomId);
            
            // 플레이어 등록
            await session.AddPlayerAsync(new Domain.ValueObjects.PlayerId(e.Player1.PlayerId));
            await session.AddPlayerAsync(new Domain.ValueObjects.PlayerId(e.Player2.PlayerId));

            _logger.LogInformation("Match found and room created: {RoomId}, Player1={P1}, Player2={P2}", 
                roomId, e.Player1.PlayerId, e.Player2.PlayerId);

            if (_sub != null)
            {
                var payload = new DistributedMatchPayload
                {
                    RoomId = roomId,
                    Player1Id = e.Player1.PlayerId,
                    Player2Id = e.Player2.PlayerId
                };
                var json = JsonSerializer.Serialize(payload);
                await _sub.PublishAsync(RedisChannel.Literal("matchmaking:matches"), json);
            }
            else
            {
                // WebSocket 엔드포인트
                var wsEndpoint = $"ws://localhost:5183/ws/game/{roomId}";

                // Player 1에게 매칭 결과 전송
                if (_waitingPlayers.TryGetValue(e.Player1.PlayerId, out var state1))
                {
                    state1.Completion.TrySetResult(new MatchmakingUpdate
                    {
                        Status = MatchmakingStatus.Matched,
                        MatchResult = new MatchFoundResult
                        {
                            RoomId = roomId,
                            PlayerSide = "A",
                            WsEndpoint = wsEndpoint,
                            OpponentId = e.Player2.PlayerId
                        }
                    });
                }

                // Player 2에게 매칭 결과 전송
                if (_waitingPlayers.TryGetValue(e.Player2.PlayerId, out var state2))
                {
                    state2.Completion.TrySetResult(new MatchmakingUpdate
                    {
                        Status = MatchmakingStatus.Matched,
                        MatchResult = new MatchFoundResult
                        {
                            RoomId = roomId,
                            PlayerSide = "B",
                            WsEndpoint = wsEndpoint,
                            OpponentId = e.Player1.PlayerId
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating match for room {RoomId}", roomId);
            if (_waitingPlayers.TryGetValue(e.Player1.PlayerId, out var state1))
            {
                state1.Completion.TrySetResult(new MatchmakingUpdate { Status = MatchmakingStatus.Error });
            }
            if (_waitingPlayers.TryGetValue(e.Player2.PlayerId, out var state2))
            {
                state2.Completion.TrySetResult(new MatchmakingUpdate { Status = MatchmakingStatus.Error });
            }
        }
    }

    /// <summary>
    /// 큐에서 나가기
    /// </summary>
    public override async Task<LeaveQueueResponse> LeaveQueue(
        LeaveQueueRequest request, ServerCallContext context)
    {
        var removed = await _queue.DequeueAsync(request.PlayerId);
        
        return new LeaveQueueResponse
        {
            Success = removed
        };
    }

    /// <summary>
    /// 큐 상태 확인
    /// </summary>
    public override async Task<QueueStatus> GetQueueStatus(
        GetQueueStatusRequest request, ServerCallContext context)
    {
        var status = await _queue.GetStatusAsync(request.PlayerId);
        
        return new QueueStatus
        {
            IsInQueue = status.IsInQueue,
            QueuePosition = status.Position,
            EstimatedWaitSeconds = status.EstimatedWaitSeconds,
            PlayersInQueue = status.TotalInQueue
        };
    }
}

public class DistributedMatchPayload
{
    public string RoomId { get; set; } = string.Empty;
    public string Player1Id { get; set; } = string.Empty;
    public string Player2Id { get; set; } = string.Empty;
}