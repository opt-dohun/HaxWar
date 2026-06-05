using System.Collections.Concurrent;
using Grpc.Core;
using HexWar.Matchmaking.Protos;

namespace HexWar.Matchmaking.Services;

public class MatchmakingQueue
{
    private readonly ConcurrentQueue<QueueEntry> _queue = new();
    private readonly object _lock = new();

    public void Enqueue(QueueEntry entry)
    {
        _queue.Enqueue(entry);
        TryMatch();
    }

    public void Remove(string playerId)
    {
        lock (_lock)
        {
            var list = _queue.ToList();
            _queue.Clear();
            foreach (var item in list)
            {
                if (item.PlayerId != playerId)
                {
                    _queue.Enqueue(item);
                }
            }
        }
    }

    private void TryMatch()
    {
        lock (_lock)
        {
            while (_queue.Count >= 2)
            {
                if (_queue.TryDequeue(out var playerA) && _queue.TryDequeue(out var playerB))
                {
                    var matchId = Guid.NewGuid().ToString();
                    var gameRoomId = $"room-{Guid.NewGuid().ToString()[..8]}";

                    playerA.MatchCompletion.TrySetResult(new MatchResult(matchId, gameRoomId, "A"));
                    playerB.MatchCompletion.TrySetResult(new MatchResult(matchId, gameRoomId, "B"));
                }
            }
        }
    }
}

public record QueueEntry(string PlayerId, int SkillRating, TaskCompletionSource<MatchResult> MatchCompletion);
public record MatchResult(string MatchId, string GameRoomId, string PlayerSide);

public class MatchmakingService : Protos.MatchmakingService.MatchmakingServiceBase
{
    private readonly MatchmakingQueue _queue;
    private readonly ILogger<MatchmakingService> _logger;

    public MatchmakingService(MatchmakingQueue queue, ILogger<MatchmakingService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public override async Task JoinQueue(
        JoinQueueRequest request,
        IServerStreamWriter<MatchmakingResponse> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("Player {PlayerId} joined the queue", request.PlayerId);

        var matchCompletion = new TaskCompletionSource<MatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new QueueEntry(request.PlayerId, request.SkillRating, matchCompletion);

        _queue.Enqueue(entry);

        context.CancellationToken.Register(() =>
        {
            _logger.LogInformation("Player {PlayerId} cancelled/disconnected", request.PlayerId);
            _queue.Remove(request.PlayerId);
            matchCompletion.TrySetCanceled();
        });

        try
        {
            await responseStream.WriteAsync(new MatchmakingResponse
            {
                Status = "Searching",
                MatchId = "",
                GameRoomId = "",
                PlayerSide = ""
            });

            var result = await matchCompletion.Task;

            _logger.LogInformation("Player {PlayerId} matched. Room: {RoomId}, Side: {Side}",
                request.PlayerId, result.GameRoomId, result.PlayerSide);

            await responseStream.WriteAsync(new MatchmakingResponse
            {
                Status = "Matched",
                MatchId = result.MatchId,
                GameRoomId = result.GameRoomId,
                PlayerSide = result.PlayerSide
            });
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Player {PlayerId} queue wait was cancelled", request.PlayerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in matchmaking for player {PlayerId}", request.PlayerId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override Task<LeaveQueueResponse> LeaveQueue(LeaveQueueRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Player {PlayerId} requested to leave the queue", request.PlayerId);
        _queue.Remove(request.PlayerId);
        return Task.FromResult(new LeaveQueueResponse { Success = true });
    }
}
