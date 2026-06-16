// src/HexWar.Application/Sessions/GameSession.cs
// HexWar.Matchmaking의 GameRoomService에서 참조하기 위해 InternalsVisibleTo 설정
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HexWar.Matchmaking")]

namespace HexWar.Application.Sessions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HexWar.Application.Commands;
using HexWar.Application.Messaging;
using HexWar.Application.Queries;
using HexWar.Application.Services;
using HexWar.Domain.Commands;
using HexWar.Domain.Entities;
using HexWar.Domain.Enums;
using HexWar.Domain.Events;
using HexWar.Domain.Exceptions;
using HexWar.Domain.ValueObjects;
using Timer = System.Timers.Timer;

/// <summary>
/// 단일 게임방의 세션을 관리합니다.
/// WebSocket 연결, 타이머, 플레이어 준비 상태를 조율하고
/// 순수 도메인 로직(GameRoom)을 호출합니다.
/// </summary>
public class GameSession : IDisposable
{
    private readonly string _roomId;
    private readonly IGameRoomRepository _repository;
    private readonly IDistributedLock? _distributedLock;
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly IGameEventPublisher? _eventPublisher;
    private readonly ILogger<GameSession>? _logger;

    // 세션 메타데이터만 유지 (GameRoom 상태 아님)
    private GamePhase _currentPhase = GamePhase.WatingForPlayers;
    private int _currentRound = 0;
    private int _maxRounds = 20;
    private Dictionary<PlayerSide, PlayerId> _players = new();

    // 플레이어 세션 상태 (연결, Planning 완료 여부 등)
    private readonly Dictionary<PlayerSide, PlayerSessionState> _playerStates;

    // 이벤트 버퍼 (재연결용)
    private readonly CircularBuffer<BufferedEvent> _eventBuffer = new(200);
    private long _eventSequence = 0;

    // Planning 타이머
    private Timer? _planningTimer;
    private readonly TimeSpan _planningTimeout;
    private int _planningTimerRound = -1;

    // 동시성 제어 (단일 서버 모드용)
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string RoomId => _roomId;
    public GamePhase CurrentPhase => _currentPhase;
    public int CurrentRound => _currentRound;
    public int ConnectedPlayerCount => _playerStates.Values.Count(s => s.IsConnected);

    public event EventHandler<GameOverEventArgs>? OnGameOver;
    public event EventHandler<RoundResolvedEventArgs>? OnRoundResolved;

    public DateTime? LastActivityAt { get; private set; }

    public TimeSpan LastActivityElapsed => LastActivityAt.HasValue
        ? DateTime.UtcNow - LastActivityAt.Value
        : TimeSpan.Zero;

    public GameSession(
        string roomId,
        IEventBroadcaster eventBroadcaster,
        IGameRoomRepository repository,
        IGameEventPublisher? eventPublisher = null,
        IDistributedLock? distributedLock = null,
        ILogger<GameSession>? logger = null,
        TimeSpan? planningTimeout = null)
    {
        _roomId = roomId;
        _eventBroadcaster = eventBroadcaster;
        _repository = repository;
        _eventPublisher = eventPublisher;
        _distributedLock = distributedLock;
        _logger = logger;
        _planningTimeout = planningTimeout ?? TimeSpan.FromSeconds(30);

        _playerStates = new()
        {
            { PlayerSide.A, new PlayerSessionState(PlayerSide.A) },
            { PlayerSide.B, new PlayerSessionState(PlayerSide.B) }
        };

        LastActivityAt = DateTime.UtcNow;

        // Redis Pub/Sub 구독 (분산 환경)
        if (_eventPublisher != null)
        {
            _eventPublisher.Subscribe(roomId, HandleRemoteEventAsync);
        }
    }

    // ============================================================
    // 핵심: GameRoom이 필요할 때만 Redis에서 로드
    // ============================================================

    /// <summary>
    /// GameRoom에 대한 독점 접근을 획득하고 작업을 실행합니다.
    /// 분산 환경: Redis 분산 락 + Redis에서 로드
    /// 단일 서버: 인메모리 락 + Redis/인메모리에서 로드
    /// </summary>
    private async Task<T?> ExecuteOnGameRoomAsync<T>(
        Func<GameRoom, Task<T>> action) where T : class
    {
        if (_distributedLock != null)
        {
            // ============================================================
            // 분산 모드: Redis 락 + Redis에서 GameRoom 로드
            // ============================================================
            using var lockHandle = await _distributedLock.TryAcquireAsync(
                $"gameroom:{_roomId}", TimeSpan.FromSeconds(5));

            if (lockHandle == null)
            {
                _logger?.LogWarning("Lock contention for room {RoomId}: Busy", _roomId);
                return null; // 락 획득 실패
            }

            // Redis에서 최신 GameRoom 로드
            var gameRoom = await _repository.GetByIdAsync(_roomId);
            if (gameRoom == null)
            {
                _logger?.LogError("GameRoom {RoomId} not found in repository", _roomId);
                return null;
            }

            // 메타데이터 동기화
            SyncMetadata(gameRoom);

            // 작업 실행
            var result = await action(gameRoom);

            // 변경된 GameRoom을 Redis에 저장
            await _repository.SaveAsync(gameRoom);

            // 메타데이터 다시 동기화
            SyncMetadata(gameRoom);

            return result;
        }
        else
        {
            // ============================================================
            // 단일 서버 모드: 인메모리 락 + 인메모리/Redis에서 로드
            // ============================================================
            await _lock.WaitAsync();
            try
            {
                var gameRoom = await _repository.GetByIdAsync(_roomId);
                if (gameRoom == null)
                {
                    return null;
                }

                SyncMetadata(gameRoom);

                var result = await action(gameRoom);

                await _repository.SaveAsync(gameRoom);
                SyncMetadata(gameRoom);

                return result;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// GameRoom 상태 → GameSession 메타데이터 동기화
    /// GameSession은 GameRoom을 소유하지 않고, 메타데이터만 캐시합니다.
    /// </summary>
    private void SyncMetadata(GameRoom gameRoom)
    {
        _currentPhase = gameRoom.Phase;
        _currentRound = gameRoom.CurrentRound;
        _maxRounds = gameRoom.MaxRounds;
        _players = new Dictionary<PlayerSide, PlayerId>(gameRoom.Players);
    }

    /// <summary>
    /// 플레이어를 게임방에 참가시킵니다.
    /// </summary>
    public async Task<string?> AddPlayerAsync(PlayerId playerId)
    {
        return await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            var side = gameRoom.AddPlayer(playerId);
            await PublishAndBroadcastEventsAsync(gameRoom);
            return side.ToString();
        });
    }

    public async Task OnPlayerConnectedAsync(PlayerSide side)
    {
        _playerStates[side].IsConnected = true;
        _playerStates[side].MarkActivity();
        LastActivityAt = DateTime.UtcNow;

        await ExecuteOnGameRoomAsync(gameRoom =>
        {
            // 계획 단계일 때 타이머가 구동 중이지 않다면 타이머 시작
            if (gameRoom.Phase == GamePhase.Planning && _planningTimer == null)
            {
                StartPlanningTimer(gameRoom.CurrentRound);
            }
            return Task.FromResult("");
        });
    }

    public Task OnPlayerDisconnectedAsync(PlayerSide side)
    {
        _playerStates[side].IsConnected = false;
        _playerStates[side].MarkActivity();
        LastActivityAt = DateTime.UtcNow;

        // 15초 유예 후에도 연결이 끊겨있으면 게임 종료
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (!_playerStates[side].IsConnected && _currentPhase != GamePhase.GameOver)
            {
                await ForceGameOverAsync(GameOverReason.PlayerDisconnected);
            }
        });

        return Task.CompletedTask;
    }

    // ============================================================
    // 명령 처리
    // ============================================================

    public async Task<MoveUnitsCommandResult> HandleMoveUnitsAsync(
        PlayerSide side, MoveCommand command)
    {
        if (!_playerStates[side].IsConnected)
            return MoveUnitsCommandResult.Fail("Not connected", "NOT_CONNECTED");

        var result = await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            if (gameRoom.Phase != GamePhase.Planning)
                throw new DomainException("Wrong phase");

            int remaining = gameRoom.GetRemainingUnits(side);
            if (remaining <= 0)
                throw new DomainException("No units remaining");

            int adjustedCount = Math.Min(command.UnitCount, remaining);
            var adjustedCommand = new MoveCommand(command.From, command.To, adjustedCount);

            var moveResult = gameRoom.MoveUnits(side, adjustedCommand);

            // 세션 상태 업데이트
            _playerStates[side].MoveCommandCompleted = gameRoom.GetRemainingUnits(side) <= 0;

            await PublishAndBroadcastEventsAsync(gameRoom);

            return MoveUnitsCommandResult.Success(
                moveResult.ActualMoved,
                moveResult.From.ToString(),
                moveResult.To.ToString());
        });

        if (result == null)
        {
            return MoveUnitsCommandResult.Fail("Server busy, please retry", "LOCK_CONTENTION");
        }

        // 해소 체크
        await CheckAndResolveIfReadyAsync();

        return result;
    }

    public async Task<EncounterDecisionCommandResult> HandleEncounterDecisionAsync(
        PlayerSide side, EdgeId edgeId, EncounterDecision decision)
    {
        if (!_playerStates[side].IsConnected)
            return EncounterDecisionCommandResult.Fail("Not connected", "NOT_CONNECTED");

        var result = await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            gameRoom.ResolveEncounter(edgeId, side, decision);

            _playerStates[side].EncounterDecisionsCompleted =
                !gameRoom.GetUndecidedEncounters(side).Any();

            await PublishAndBroadcastEventsAsync(gameRoom);

            return EncounterDecisionCommandResult.Success(
                edgeId.ToString(),
                decision.ToString(),
                !gameRoom.HasPendingEncounterOn(edgeId));
        });

        if (result == null)
        {
            return EncounterDecisionCommandResult.Fail("Server busy, please retry", "LOCK_CONTENTION");
        }

        await CheckAndResolveIfReadyAsync();

        return result;
    }

    // ============================================================
    // 게임 상태 조회
    // ============================================================

    public async Task<GameStateView?> GetGameStateForPlayerAsync(PlayerSide side)
    {
        return await ExecuteOnGameRoomAsync(gameRoom =>
        {
            var enemySide = side == PlayerSide.A ? PlayerSide.B : PlayerSide.A;
            bool hasHQVision = HasHeadquartersVision(side, gameRoom);

            var view = new GameStateView
            {
                RoomId = RoomId,
                Phase = gameRoom.Phase.ToString(),
                CurrentRound = gameRoom.CurrentRound,
                MaxRounds = gameRoom.MaxRounds,
                MySide = side.ToString(),

                // Planning 상태
                MyRemainingUnits = gameRoom.GetRemainingUnits(side),
                MyPendingMoves = gameRoom.GetPendingMoves(side)
                    .Select(m => new PendingMoveView
                    {
                        FromNodeId = m.From.Value,
                        ToNodeId = m.To.Value,
                        UnitCount = m.Count
                    }).ToList(),
                MoveCommandCompleted = IsMoveCommandCompleted(side, gameRoom),
                EncounterDecisionsCompleted = _playerStates[side].EncounterDecisionsCompleted || !gameRoom.GetUndecidedEncounters(side).Any(),
                IsMyPlanningComplete = IsMoveCommandCompleted(side, gameRoom) && (_playerStates[side].EncounterDecisionsCompleted || !gameRoom.GetUndecidedEncounters(side).Any()),

                // 노드
                Nodes = gameRoom.Nodes.Values.Select(node =>
                    BuildNodeView(node, side, enemySide, hasHQVision)).ToList(),

                // 간선
                Edges = gameRoom.Edges.Values.Select(edge =>
                    BuildEdgeView(edge, side, enemySide)).ToList(),

                // 조우
                PendingEncounters = gameRoom.PendingEncounters.Select(pending =>
                    BuildPendingEncounterView(pending, side, enemySide, gameRoom)).ToList(),

                // 미결정 조우
                UndecidedEncounterEdgeIds = gameRoom.GetUndecidedEncounters(side)
                    .Select(e => e.EdgeId.ToString())
                    .ToList(),

                // 게임 종료
                IsGameOver = gameRoom.Phase == GamePhase.GameOver,
                Winner = BuildWinnerString(gameRoom),
                Scores = new Dictionary<string, int>
                {
                    { PlayerSide.A.ToString(), gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA) },
                    { PlayerSide.B.ToString(), gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB) }
                }
            };

            return Task.FromResult(view);
        });
    }

    // View 빌더 헬퍼
    private NodeView BuildNodeView(Node node, PlayerSide side, PlayerSide enemySide, bool hasHQVision)
    {
        bool isOwned = node.Ownership == NodeOwnershipExtensions.FromPlayerSide(side);
        bool fullVision = node.IsHeadquarters || hasHQVision || isOwned;

        var myUnits = node.Units[side];
        var enemyUnits = node.Units[enemySide];

        return new NodeView
        {
            Id = node.Id.Value,
            Name = node.Name,
            Type = node.Type.ToString(),
            Ownership = node.Ownership.ToString(),
            IsHeadquarters = node.IsHeadquarters,
            IsSupplyLine = node.IsSupplyLine,
            Neighbors = node.Neighbors.Select(n => n.Value).ToList(),
            IsOwnedByMe = isOwned,

            MyUnits = new UnitGroupDetailView
            {
                Mobile = myUnits.MobileCount,
                Garrison = myUnits.GarrisonCount,
                Total = myUnits.TotalCount
            },

            EnemyUnits = new EnemyUnitDetailView
            {
                TotalCount = enemyUnits.TotalCount,
                MobileCount = fullVision ? enemyUnits.MobileCount : null,
                GarrisonCount = fullVision ? enemyUnits.GarrisonCount : null,
                IsFullVisibility = fullVision
            },

            RecentDepartures = node.RecentDepartures
                .Select(d => new DepartureView
                {
                    FromNodeId = d.FromNode.Value,
                    UnitCount = d.UnitCount
                }).ToList()
        };
    }

    private EdgeView BuildEdgeView(Edge edge, PlayerSide side, PlayerSide enemySide)
    {
        return new EdgeView
        {
            FromNodeId = edge.From.Value,
            ToNodeId = edge.To.Value,
            Distance = edge.Distance.RoundsRequired,
            HasMyUnitsTraveling = edge.HasUnitsOfSide(side),
            HasEncounter = edge.HasEncounterWaiting(),

            // 조우 발생 시에만 상대 유닛 존재 확인 가능
            HasEnemyUnitsTraveling = edge.HasEncounterWaiting()
                ? edge.HasUnitsOfSide(enemySide)
                : null,

            // 내 이동 중인 유닛 상세
            MyTravelingUnits = edge.TravelingUnits
                .SelectMany(kvp => kvp.Value
                    .Where(g => g.Side == side)
                    .Select(g => new TravelingUnitView
                    {
                        UnitCount = g.UnitCount,
                        DestinationNodeId = g.Destination.Value,
                        RemainingRounds = kvp.Key
                    }))
                .ToList()
        };
    }

    private PendingEncounterDetailView BuildPendingEncounterView(
        PendingEncounter pending, PlayerSide side, PlayerSide enemySide, GameRoom gameRoom)
    {
        var myGroup = pending.GroupA.Side == side ? pending.GroupA : pending.GroupB;
        var enemyGroup = pending.GroupA.Side == side ? pending.GroupB : pending.GroupA;

        return new PendingEncounterDetailView
        {
            EdgeId = pending.EdgeId.ToString(),
            FromNodeId = gameRoom.Edges[pending.EdgeId].From.Value,
            ToNodeId = gameRoom.Edges[pending.EdgeId].To.Value,

            MyUnitCount = myGroup.UnitCount,
            MyDestinationNodeId = myGroup.Destination.Value,
            EnemyUnitCount = enemyGroup.UnitCount,

            RemainingRounds = pending.RemainingRounds,
            IHaveDecided = pending.HasDecided(side),
            MyDecision = pending.GetDecision(side)?.ToString(),

            IsResolved = pending.BothDecided,
            EnemyDecision = pending.BothDecided
                ? pending.GetDecision(enemySide)?.ToString()
                : null
        };
    }

    // 해소 트리거
    private async Task CheckAndResolveIfReadyAsync()
    {
        await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            if (!gameRoom.IsReadyForResolution())
                return "";

            // 로컬 서버의 플레이어 소켓 연결 여부와 상관없이 비즈니스 상태 기준으로만 해소 판단
            bool allReady = new[] { PlayerSide.A, PlayerSide.B }
                .All(side => IsMoveCommandCompleted(side, gameRoom) && !gameRoom.GetUndecidedEncounters(side).Any());

            if (allReady)
            {
                await ResolveRoundAsync(gameRoom);
            }
            return "";
        });
    }

    private async Task ResolveRoundAsync(GameRoom gameRoom)
    {
        StopPlanningTimer();

        var result = gameRoom.ResolveRound();

        await PublishAndBroadcastEventsAsync(gameRoom);

        foreach (var state in _playerStates.Values)
            state.ResetForNewRound();

        OnRoundResolved?.Invoke(this, new RoundResolvedEventArgs(RoomId, gameRoom.CurrentRound - 1));

        if (result.GameOver)
        {
            await HandleGameOverAsync(gameRoom, result.Winner, GameOverReason.AllNodesCaptured);
        }
        else
        {
            StartPlanningTimer(gameRoom.CurrentRound);
        }
    }

    // 타이머 관리
    public void StartPlanningTimer(int forRound)
    {
        StopPlanningTimer();

        _planningTimerRound = forRound;
        _planningTimer = new Timer(_planningTimeout.TotalMilliseconds);
        _planningTimer.Elapsed += async (_, _) => await OnPlanningTimeoutAsync(forRound);
        _planningTimer.AutoReset = false;
        _planningTimer.Start();
    }

    private async Task OnPlanningTimeoutAsync(int forRound)
    {
        await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            // 락 획득 후 Redis 데이터 라운드가 타이머를 구동할 당시의 라운드 정보와 다르면 무시
            if (gameRoom.Phase != GamePhase.Planning || gameRoom.CurrentRound != forRound) 
                return "";

            // 미결정 조우는 기본값(Retreat)으로 자동 처리
            foreach (var side in new[] { PlayerSide.A, PlayerSide.B })
            {
                var undecided = gameRoom.GetUndecidedEncounters(side);
                foreach (var enc in undecided)
                {
                    gameRoom.ResolveEncounter(enc.EdgeId, side, EncounterDecision.Retreat);
                }
            }

            await ResolveRoundAsync(gameRoom);
            return "";
        });
    }

    private void StopPlanningTimer()
    {
        _planningTimer?.Stop();
        _planningTimer?.Dispose();
        _planningTimer = null;
    }

    // 게임 종료
    private Task HandleGameOverAsync(GameRoom gameRoom, PlayerSide? winner, GameOverReason reason)
    {
        StopPlanningTimer();
        OnGameOver?.Invoke(this, new GameOverEventArgs(RoomId, winner, reason));
        return Task.CompletedTask;
    }

    private async Task ForceGameOverAsync(GameOverReason reason)
    {
        await ExecuteOnGameRoomAsync(async gameRoom =>
        {
            if (gameRoom.Phase == GamePhase.GameOver) return "";

            PlayerSide? winner = reason == GameOverReason.PlayerDisconnected
                ? _playerStates.First(s => s.Value.IsConnected).Key
                : null;

            gameRoom.ForceGameOver(winner, reason);

            await PublishAndBroadcastEventsAsync(gameRoom);

            StopPlanningTimer();
            OnGameOver?.Invoke(this, new GameOverEventArgs(RoomId, winner, reason));

            return "";
        });
    }

    // 이벤트 브로드캐스트
    private async Task PublishAndBroadcastEventsAsync(GameRoom gameRoom)
    {
        var events = gameRoom.FlushDomainEvents();
        if (!events.Any()) return;

        foreach (var domainEvent in events)
        {
            var sequenceNumber = Interlocked.Increment(ref _eventSequence);
            _eventBuffer.Add(new BufferedEvent
            {
                SequenceNumber = sequenceNumber,
                Event = domainEvent,
                Timestamp = DateTime.UtcNow
            });

            await BroadcastSingleEventAsync(domainEvent, sequenceNumber);
        }
    }

    // [버퍼링된 이벤트 조회] 재연결 클라이언트용 메서드 
    public List<BufferedEvent> GetEventsAfter(long lastSeenSequence)
    {
        return _eventBuffer
            .Where(e => e.SequenceNumber > lastSeenSequence)
            .OrderBy(e => e.SequenceNumber)
            .ToList();
    }

    // 마지막 시퀀스 번호 반환     
    public long GetLastSequenceNumber() => _eventSequence;

    /// <summary>
    /// 다른 서버에서 발행된 이벤트 처리
    /// </summary>
    private async Task HandleRemoteEventAsync(string eventJson)
    {
        try
        {
            var message = JsonSerializer.Deserialize<DistributedEventMessage>(eventJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
            if (message == null) return;

            // 자기 서버가 보낸 이벤트는 무시
            if (message.SourceServerId == Environment.MachineName) return;

            // 메타데이터 업데이트 및 타이머 정렬
            if (message.EventType == nameof(RoundResolved))
            {
                var resolved = JsonSerializer.Deserialize<RoundResolved>(message.EventData.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });
                if (resolved != null)
                {
                    _currentPhase = GamePhase.Planning;
                    _currentRound = resolved.CompletedRound + 1;

                    // 원격에서 라운드가 해소되었으므로 로컬 타이머 중지 및 새 라운드 타이머 구동
                    StartPlanningTimer(_currentRound);
                }
            }
            else if (message.EventType == nameof(GameOver))
            {
                _currentPhase = GamePhase.GameOver;
                StopPlanningTimer();
            }

            // 이벤트를 클라이언트에게 WebSocket으로 전송
            var eventType = Type.GetType($"HexWar.Domain.Events.{message.EventType}, HexWar.Domain");
            if (eventType == null) return;

            var domainEvent = JsonSerializer.Deserialize(
                message.EventData.GetRawText(), eventType, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                }) as IDomainEvent;

            if (domainEvent == null) return;

            await _eventBroadcaster.BroadcastToRoomAsync(RoomId, domainEvent, message.SequenceNumber);

            // CircularBuffer에도 저장 (재연결 복구용)
            _eventBuffer.Add(new BufferedEvent
            {
                SequenceNumber = message.SequenceNumber,
                Event = domainEvent,
                Timestamp = message.Timestamp
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling remote event for room {RoomId}", RoomId);
        }
    }

    private async Task BroadcastSingleEventAsync(IDomainEvent domainEvent, long sequenceNumber)
    {
        switch (domainEvent)
        {
            // RoundResolved 하나로 모든 해소 결과 전달
            case RoundResolved resolved:
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, resolved, sequenceNumber);
                break;

            // Planning 단계 이벤트만 개별 전송
            case UnitsDeparted departed:
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, departed.ToPublicInfo(), sequenceNumber);
                break;

            case EncounterDecisionMade decisionMade:
                await _eventBroadcaster.SendToPlayerAsync(RoomId, decisionMade.DecidingPlayer, decisionMade, sequenceNumber);
                break;

            case GameStarted:
            case RoundStarted:
            case GameOver:
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, domainEvent, sequenceNumber);
                break;

            default:
                break;
        }

        // Redis Pub/Sub 발행 (다른 서버에 연결된 클라이언트용)
        if (_eventPublisher != null)
        {
            await _eventPublisher.PublishAsync(RoomId, domainEvent, sequenceNumber);
        }
    }

    // 유틸리티
    private bool HasHeadquartersVision(PlayerSide side, GameRoom gameRoom)
    {
        var hq = gameRoom.Nodes.Values.FirstOrDefault(n => n.IsHeadquarters);
        return hq != null && hq.GetTotalCount(side) > 0;
    }

    private string? BuildWinnerString(GameRoom gameRoom)
    {
        if (gameRoom.Phase != GamePhase.GameOver) return null;

        int nodesA = gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA);
        int nodesB = gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB);

        if (nodesA > nodesB) return PlayerSide.A.ToString();
        if (nodesB > nodesA) return PlayerSide.B.ToString();
        return "Draw";
    }

    private bool IsMoveCommandCompleted(PlayerSide side, GameRoom gameRoom)
    {
        return _playerStates[side].MoveCommandCompleted ||
               gameRoom.GetRemainingUnits(side) <= 0 ||
               gameRoom.Nodes.Values.Sum(n => n.GetMobileCount(side)) == 0;
    }

    public void Dispose()
    {
        StopPlanningTimer();

        if (_eventPublisher != null)
        {
            _eventPublisher.Unsubscribe(_roomId);
        }

        _eventBuffer.Dispose();
        _lock.Dispose();

        OnGameOver = null;
        OnRoundResolved = null;
    }
}