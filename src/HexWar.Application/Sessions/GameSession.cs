// src/HexWar.Application/Sessions/GameSession.cs
namespace HexWar.Application.Sessions;

using System.Timers;
using HexWar.Application.Commands;
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
    private readonly GameRoom _gameRoom;
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly IGameRoomRepository _repository;
    private readonly Dictionary<PlayerSide, PlayerSessionState> _playerStates;

    private Timer? _planningTimer;
    private readonly TimeSpan _planningTimeout;
    private const int DefaultPlanningTimeoutSeconds = 30;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string RoomId => _gameRoom.RoomId;
    public GamePhase CurrentPhase => _gameRoom.Phase;
    public int CurrentRound => _gameRoom.CurrentRound;

    public event EventHandler<GameOverEventArgs>? OnGameOver;
    public event EventHandler<RoundResolvedEventArgs>? OnRoundResolved;

    public GameSession(
        GameRoom gameRoom,
        IEventBroadcaster eventBroadcaster,
        IGameRoomRepository repository,
        TimeSpan? planningTimeout = null)
    {
        _gameRoom = gameRoom ?? throw new ArgumentNullException(nameof(gameRoom));
        _eventBroadcaster = eventBroadcaster ?? throw new ArgumentNullException(nameof(eventBroadcaster));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _planningTimeout = planningTimeout ?? TimeSpan.FromSeconds(DefaultPlanningTimeoutSeconds);

        _playerStates = new()
        {
            { PlayerSide.A, new PlayerSessionState(PlayerSide.A) },
            { PlayerSide.B, new PlayerSessionState(PlayerSide.B) }
        };
    }

    // 연결 관리
    public async Task OnPlayerConnectedAsync(PlayerSide side)
    {
        await _lock.WaitAsync();
        try
        {
            _playerStates[side].IsConnected = true;
            _playerStates[side].MarkActivity();
            await _repository.SaveAsync(_gameRoom);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnPlayerDisconnectedAsync(PlayerSide side)
    {
        await _lock.WaitAsync();
        try
        {
            _playerStates[side].IsConnected = false;
            _playerStates[side].MarkActivity();

            // 15초 유예 후에도 연결이 끊겨있으면 게임 종료
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                if (!_playerStates[side].IsConnected && _gameRoom.Phase != GamePhase.GameOver)
                {
                    await ForceGameOverAsync(GameOverReason.PlayerDisconnected);
                }
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    // 명령 처리
    public async Task<MoveUnitsCommandResult> HandleMoveUnitsAsync(PlayerSide side, MoveCommand command)
    {
        await _lock.WaitAsync();
        try
        {
            // 1차: 연결 확인
            if (!_playerStates[side].IsConnected)
                return MoveUnitsCommandResult.Fail("Not connected", "NOT_CONNECTED");

            // 2차: 페이즈 확인
            if (_gameRoom.Phase != GamePhase.Planning)
                return MoveUnitsCommandResult.Fail("Wrong phase", "WRONG_PHASE");

            // 3차: 유닛 수 확인
            int remaining = _gameRoom.GetRemainingUnits(side);
            if (remaining <= 0)
                return MoveUnitsCommandResult.Fail("No units remaining", "NO_UNITS");

            // 요청 수 조정
            int adjustedCount = Math.Min(command.UnitCount, remaining);
            var adjustedCommand = new MoveCommand(command.From, command.To, adjustedCount);

            // 4차: 도메인 실행
            var moveResult = _gameRoom.MoveUnits(side, adjustedCommand);

            // 상태 업데이트
            _playerStates[side].MoveCommandCompleted = _gameRoom.GetRemainingUnits(side) <= 0;

            // 이벤트 브로드캐스트
            await BroadcastEventsAsync();

            // 해소 체크
            await CheckAndResolveIfReadyAsync();
            await _repository.SaveAsync(_gameRoom);

            return MoveUnitsCommandResult.Success(
                moveResult.ActualMoved,
                moveResult.From.ToString(),
                moveResult.To.ToString());
        }
        catch (DomainException ex)
        {
            return MoveUnitsCommandResult.Fail(ex.Message, "DOMAIN_ERROR");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<EncounterDecisionCommandResult> HandleEncounterDecisionAsync(
        PlayerSide side, EdgeId edgeId, EncounterDecision decision)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_playerStates[side].IsConnected)
                return EncounterDecisionCommandResult.Fail("Not connected", "NOT_CONNECTED");

            _gameRoom.ResolveEncounter(edgeId, side, decision);

            _playerStates[side].EncounterDecisionsCompleted =
                !_gameRoom.GetUndecidedEncounters(side).Any();

            await BroadcastEventsAsync();
            await CheckAndResolveIfReadyAsync();
            await _repository.SaveAsync(_gameRoom);

            return EncounterDecisionCommandResult.Success(
                edgeId.ToString(),
                decision.ToString(),
                !_gameRoom.HasPendingEncounterOn(edgeId));
        }
        catch (DomainException ex)
        {
            return EncounterDecisionCommandResult.Fail(ex.Message, "DOMAIN_ERROR");
        }
        finally
        {
            _lock.Release();
        }
    }

    // 게임 상태 조회
    public GameStateView GetGameStateForPlayer(PlayerSide side)
    {
        var enemySide = side == PlayerSide.A ? PlayerSide.B : PlayerSide.A;
        bool hasHQVision = HasHeadquartersVision(side);

        return new GameStateView
        {
            RoomId = RoomId,
            Phase = _gameRoom.Phase.ToString(),
            CurrentRound = _gameRoom.CurrentRound,
            MaxRounds = _gameRoom.MaxRounds,
            MySide = side.ToString(),

            // Planning 상태
            MyRemainingUnits = _gameRoom.GetRemainingUnits(side),
            MyPendingMoves = _gameRoom.GetPendingMoves(side)
                .Select(m => new PendingMoveView
                {
                    FromNodeId = m.From.Value,
                    ToNodeId = m.To.Value,
                    UnitCount = m.Count
                }).ToList(),
            MoveCommandCompleted = _playerStates[side].MoveCommandCompleted,
            EncounterDecisionsCompleted = _playerStates[side].EncounterDecisionsCompleted || !_gameRoom.GetUndecidedEncounters(side).Any(),
            IsMyPlanningComplete = _playerStates[side].MoveCommandCompleted && (_playerStates[side].EncounterDecisionsCompleted || !_gameRoom.GetUndecidedEncounters(side).Any()),

            // 노드
            Nodes = _gameRoom.Nodes.Values.Select(node =>
                BuildNodeView(node, side, enemySide, hasHQVision)).ToList(),

            // 간선
            Edges = _gameRoom.Edges.Values.Select(edge =>
                BuildEdgeView(edge, side, enemySide)).ToList(),

            // 조우
            PendingEncounters = _gameRoom.PendingEncounters.Select(pending =>
                BuildPendingEncounterView(pending, side, enemySide)).ToList(),

            // 미결정 조우
            UndecidedEncounterEdgeIds = _gameRoom.GetUndecidedEncounters(side)
                .Select(e => e.EdgeId.ToString())
                .ToList(),

            // 게임 종료
            IsGameOver = _gameRoom.Phase == GamePhase.GameOver,
            Winner = BuildWinnerString(),
            Scores = new Dictionary<string, int>
            {
                { PlayerSide.A.ToString(), _gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA) },
                { PlayerSide.B.ToString(), _gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB) }
            }
        };
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
        PendingEncounter pending, PlayerSide side, PlayerSide enemySide)
    {
        var myGroup = pending.GroupA.Side == side ? pending.GroupA : pending.GroupB;
        var enemyGroup = pending.GroupA.Side == side ? pending.GroupB : pending.GroupA;

        return new PendingEncounterDetailView
        {
            EdgeId = pending.EdgeId.ToString(),
            FromNodeId = _gameRoom.Edges[pending.EdgeId].From.Value,
            ToNodeId = _gameRoom.Edges[pending.EdgeId].To.Value,

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
        if (!_gameRoom.IsReadyForResolution())
            return;

        bool allReady = _playerStates.Values
            .Where(s => s.IsConnected)
            .All(s => s.MoveCommandCompleted && (s.EncounterDecisionsCompleted || !_gameRoom.GetUndecidedEncounters(s.Side).Any()));

        if (allReady)
        {
            await ResolveRoundAsync();
        }
    }

    private async Task ResolveRoundAsync()
    {
        StopPlanningTimer();

        var result = _gameRoom.ResolveRound();

        await BroadcastEventsAsync();

        foreach (var state in _playerStates.Values)
            state.ResetForNewRound();

        OnRoundResolved?.Invoke(this, new RoundResolvedEventArgs(RoomId, _gameRoom.CurrentRound - 1));

        await _repository.SaveAsync(_gameRoom);

        if (result.GameOver)
        {
            await HandleGameOverAsync(result.Winner, GameOverReason.AllNodesCaptured);
        }
        else
        {
            StartPlanningTimer();
        }
    }

    // 타이머 관리
    public void StartPlanningTimer()
    {
        StopPlanningTimer();

        _planningTimer = new Timer(_planningTimeout.TotalMilliseconds);
        _planningTimer.Elapsed += async (_, _) => await OnPlanningTimeoutAsync();
        _planningTimer.AutoReset = false;
        _planningTimer.Start();
    }

    private async Task OnPlanningTimeoutAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_gameRoom.Phase != GamePhase.Planning) return;

            // 미결정 조우는 기본값(Retreat)으로 자동 처리
            foreach (var side in new[] { PlayerSide.A, PlayerSide.B })
            {
                if (!_playerStates[side].IsConnected) continue;

                var undecided = _gameRoom.GetUndecidedEncounters(side);
                foreach (var enc in undecided)
                {
                    _gameRoom.ResolveEncounter(enc.EdgeId, side, EncounterDecision.Retreat);
                }
            }

            await ResolveRoundAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StopPlanningTimer()
    {
        _planningTimer?.Stop();
        _planningTimer?.Dispose();
        _planningTimer = null;
    }

    // 게임 종료
    private async Task HandleGameOverAsync(PlayerSide? winner, GameOverReason reason)
    {
        StopPlanningTimer();
        OnGameOver?.Invoke(this, new GameOverEventArgs(RoomId, winner, reason));
        await _repository.SaveAsync(_gameRoom);
    }

    private async Task ForceGameOverAsync(GameOverReason reason)
    {
        await _lock.WaitAsync();
        try
        {
            if (_gameRoom.Phase == GamePhase.GameOver) return;

            PlayerSide? winner = reason == GameOverReason.PlayerDisconnected
                ? _playerStates.First(s => s.Value.IsConnected).Key
                : null;

            await HandleGameOverAsync(winner, reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    // 이벤트 브로드캐스트
    private async Task BroadcastEventsAsync()
    {
        var events = _gameRoom.DomainEvents.ToList();
        if (!events.Any()) return;

        foreach (var domainEvent in events)
        {
            await BroadcastSingleEventAsync(domainEvent);
        }

        _gameRoom.ClearDomainEvents();
    }

    private async Task BroadcastSingleEventAsync(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case GameStarted:
            case RoundStarted:
            case RoundResolved:
            case GameOver:
            case UnitsRetreated:
            case UnitsAdvanced:
            case EncounterOccurred:
            case EncounterResolved:
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, domainEvent);
                break;

            case UnitsDeparted departed:
                // 제네릭 오버로드로 DTO 전송
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, departed.ToPublicInfo());
                break;

            case UnitsArrived arrived:
                await BroadcastUnitsArrivedAsync(arrived);
                break;

            case EncounterDecisionMade decisionMade:
                await _eventBroadcaster.SendToPlayerAsync(RoomId, decisionMade.DecidingPlayer, domainEvent);
                break;

            default:
                await _eventBroadcaster.BroadcastToRoomAsync(RoomId, domainEvent);
                break;
        }
    }

    private async Task BroadcastUnitsArrivedAsync(UnitsArrived arrived)
    {
        foreach (var side in new[] { PlayerSide.A, PlayerSide.B })
        {
            if (!_gameRoom.Players.ContainsKey(side)) continue;

            bool hasHQVision = HasHeadquartersVision(side);
            bool isOwnUnits = arrived.Side == side;

            if (hasHQVision || isOwnUnits)
            {
                await _eventBroadcaster.SendToPlayerAsync(RoomId, side, arrived);
            }
            else
            {
                var summary = new UnitsArrivedSummary(
                    RoomId,
                    arrived.DestinationNode,
                    arrived.Side,
                    arrived.UnitCount,
                    arrived.RoundNumber
                );
                await _eventBroadcaster.SendToPlayerAsync(RoomId, side, summary);
            }
        }
    }

    // 유틸리티
    private bool HasHeadquartersVision(PlayerSide side)
    {
        var hq = _gameRoom.Nodes.Values.FirstOrDefault(n => n.IsHeadquarters);
        return hq != null && hq.GetTotalCount(side) > 0;
    }

    private string? BuildWinnerString()
    {
        if (_gameRoom.Phase != GamePhase.GameOver) return null;

        int nodesA = _gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA);
        int nodesB = _gameRoom.Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB);

        if (nodesA > nodesB) return PlayerSide.A.ToString();
        if (nodesB > nodesA) return PlayerSide.B.ToString();
        return "Draw";
    }

    public void Dispose()
    {
        StopPlanningTimer();
        _lock.Dispose();
    }
}