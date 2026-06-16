using HexWar.Application.Sessions;
using HexWar.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace HexWar.Server.Controllers;

[ApiController]
[Route("api/matchmaking")]
public class MatchmakingApiController : ControllerBase
{
    private readonly SessionRegistry _sessionRegistry;
    private readonly IDatabase? _db;
    
    // Thread-safe dictionary for standalone fallback
    private static readonly ConcurrentDictionary<string, string> _waitingRooms = new();

    public MatchmakingApiController(SessionRegistry sessionRegistry, StackExchange.Redis.IConnectionMultiplexer? redis = null)
    {
        _sessionRegistry = sessionRegistry;
        _db = redis?.GetDatabase();
    }

    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] JoinRequest request)
    {
        if (string.IsNullOrEmpty(request.PlayerId))
        {
            return BadRequest("PlayerId is required");
        }

        // Example playerId formats: "load-A-0", "load-B-0"
        var parts = request.PlayerId.Split('-');
        if (parts.Length == 3)
        {
            var side = parts[1]; // "A" or "B"
            var index = parts[2]; // "0", "1", etc.

            if (side == "A")
            {
                var roomId = Guid.NewGuid().ToString("N")[..8];
                
                // Create game session
                var session = await _sessionRegistry.CreateSessionAsync(roomId);
                await session.AddPlayerAsync(new PlayerId(request.PlayerId));

                if (_db != null)
                {
                    await _db.StringSetAsync($"matchmaking:waiting_room:{index}", roomId, TimeSpan.FromSeconds(30));
                }
                else
                {
                    _waitingRooms[index] = roomId;
                }

                return Ok(new MatchResponse
                {
                    RoomId = roomId,
                    PlayerSide = "A",
                    OpponentId = $"load-B-{index}"
                });
            }
            else if (side == "B")
            {
                // Wait for Player A to create the room
                string? roomId = null;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (_db != null)
                    {
                        var redisRoomId = await _db.StringGetAsync($"matchmaking:waiting_room:{index}");
                        if (redisRoomId.HasValue)
                        {
                            roomId = redisRoomId.ToString();
                            break;
                        }
                    }
                    else
                    {
                        if (_waitingRooms.TryGetValue(index, out roomId))
                        {
                            break;
                        }
                    }
                    await Task.Delay(100);
                }

                if (roomId != null)
                {
                    var session = await _sessionRegistry.GetOrCreateSessionAsync(roomId);
                    if (session != null)
                    {
                        await session.AddPlayerAsync(new PlayerId(request.PlayerId));
                    }
                    
                    if (_db != null)
                    {
                        await _db.KeyDeleteAsync($"matchmaking:waiting_room:{index}");
                    }
                    else
                    {
                        _waitingRooms.TryRemove(index, out _);
                    }

                    return Ok(new MatchResponse
                    {
                        RoomId = roomId,
                        PlayerSide = "B",
                        OpponentId = $"load-A-{index}"
                    });
                }
            }
        }

        return BadRequest("Invalid player id pattern for load test matchmaking API");
    }

    public class JoinRequest
    {
        public string PlayerId { get; set; } = string.Empty;
    }

    public class MatchResponse
    {
        public string RoomId { get; set; } = string.Empty;
        public string PlayerSide { get; set; } = string.Empty;
        public string OpponentId { get; set; } = string.Empty;
    }
}
