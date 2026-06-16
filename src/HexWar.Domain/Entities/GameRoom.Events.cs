namespace HexWar.Domain.Entities;

using HexWar.Domain.Commands;
using HexWar.Domain.Enums;
using HexWar.Domain.Events;
using HexWar.Domain.Exceptions;
using HexWar.Domain.ValueObjects;

public partial class GameRoom
{
    private List<IDomainEvent> _domainEvents = new();
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    private List<EncounterOccurred> _encounterEvents = new();
    private List<NodeOwnershipChanged> _ownershipChanges = new();
    private List<ArrivalRecord> _arrivalRecords = new();
    private List<MoveExecutionRecord> _moveExecutionRecords = new();



    private void RaiseEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    private void ClearEvents()
    {
        _domainEvents.Clear();
        _encounterEvents.Clear();
        _ownershipChanges.Clear();
        _arrivalRecords.Clear();
        _moveExecutionRecords.Clear();
    }

    private void RecordEncounter(EncounterOccurred encounter)
    {
        _encounterEvents.Add(encounter);
        RaiseEvent(encounter);
    }

    private void RecordOwnershipChange(NodeId nodeId, NodeOwnership previous, NodeOwnership current, bool isSupplyLine)
    {
        var change = new NodeOwnershipChanged(nodeId, previous, current, isSupplyLine);
        _ownershipChanges.Add(change);
    }

    private void RecordArrival(NodeId destination, PlayerSide side, int count, EdgeId viaEdge)
    {
        _arrivalRecords.Add(new ArrivalRecord(destination, side, count, viaEdge));
    }

    private void RecordMoveExecution(PlayerSide side, NodeId from, NodeId to, int count)
    {
        _moveExecutionRecords.Add(new MoveExecutionRecord(from, to, side, count));
    }

    // 
    public RoundResolutionResult ResolveRound(DateTime? nextRoundDeadline = null)
    {
        if (Phase != GamePhase.Planning)
            throw new DomainException("Cannot resolve round outside Planning phase");

        Phase = GamePhase.Resolution;
        ClearEvents();
        var result = new RoundResolutionResult();

        // RoundResolved의 레퍼런스로 사용 될 로컬 컬렉션
        // RoundResolved를 기준으로 event 정보가 기록됨으로 하위 이벤트들은 로컬에서 관리
        var moveExecutions = new List<MoveExecutionRecord>();
        var arrivalRecords = new List<ArrivalRecord>();
        var encounterRecords = new List<EncounterRecord>();
        var ownershipChanges = new List<OwnershipChangeRecord>();
        var resolvedEncounters = new List<EncounterResolvedRecord>();

        // 1단계: 예약된 이동 일괄 실행
        foreach (var side in new[] { PlayerSide.A, PlayerSide.B })
        {
            foreach (var move in _pendingMoves[side])
            {
                var sourceNode = Nodes[move.From];

                // 예약된 내용에 따라 유닛 차감
                sourceNode.DepartMobileUnits(side, move.Count);

                // 간선에 배치
                var edgeId = new EdgeId(move.From, move.To);
                Edges[edgeId].StartTravel(side, move.Count, move.To);

                moveExecutions.Add(new MoveExecutionRecord(move.From, move.To, side, move.Count));
            }
        }

        // 2단계: 모든 간선에서 1라운드 진행
        var arrivals = ProcessAllEdgeAdvances();

        // 3단계: 조우 확인
        var encounters = FindAllEncounters();

        foreach (var enc in encounters)
        {
            // edge 기본 정보 획득
            var edge = Edges[enc.EdgeId];


            // 인카운트 이벤트 생성 
            encounterRecords.Add(new EncounterRecord
            {
                EdgeId = enc.EdgeId.ToString(),
                FromNode = edge.From.Value,
                ToNode = edge.To.Value,
                ParticipantAUnits = enc.GroupA.UnitCount,
                ParticipantBUnits = enc.GroupB.UnitCount,
                RemainingRounds = enc.RemainingRounds
            });

            // PendingEncounters에 등록 (다음 라운드에서 결정)
            var groupA = enc.GroupA;
            var groupB = enc.GroupB;
            PendingEncounters.Add(new PendingEncounter(enc.EdgeId, groupA, groupB, enc.RemainingRounds));
        }

        // 4단계: 조우 없는 유닛만 도착 처리
        var blockedUnits = GetBlockedUnits(encounters);

        foreach (var arrival in arrivals)
        {
            if (!blockedUnits.Contains(arrival))
            {
                var node = Nodes[arrival.Destination];
                var previousOwnership = node.Ownership;

                node.ArriveMobileUnits(arrival.Side, arrival.Count);

                arrivalRecords.Add(new ArrivalRecord(arrival.Destination, arrival.Side, arrival.Count, arrival.ViaEdge));

                if (node.Ownership != previousOwnership)
                {
                    ownershipChanges.Add(new OwnershipChangeRecord
                    {
                        NodeId = arrival.Destination.Value,
                        PreviousOwner = previousOwnership.ToString(),
                        NewOwner = node.Ownership.ToString(),
                        IsSupplyLineActive = node.IsSupplyLine
                    });
                }
            }
        }

        // 5단계: 조우 Pending 등록
        // 두 플레이 모두 결정을 진행한 경우
        var newlyResolved = PendingEncounters.Where(p => p.BothDecided).ToList();

        foreach (var pending in newlyResolved)
        {
            resolvedEncounters.Add(new EncounterResolvedRecord
            {
                EdgeId = pending.EdgeId.ToString(),
                DecisionA = pending.DecisionA.ToString()!,
                DecisionB = pending.DecisionB.ToString()!,
                OutcomeAUnits = pending.OutcomeA?.UnitCount ?? 0,
                OutcomeBUnits = pending.OutcomeB?.UnitCount ?? 0,
                OutcomeADirection = pending.OutcomeA?.DestinationNode.ToString() ?? "",
                OutcomeBDirection = pending.OutcomeB?.DestinationNode.ToString() ?? ""
            });
        }

        // 6단계: 노드 상태 스냅샷 생성
        var nodeSnapshots = Nodes.Values.ToDictionary(
            n => n.Id.Value,
            n => new NodeStateSnapshot
            {
                NodeId = n.Id.Value,
                Ownership = n.Ownership.ToString(),
                PlayerAMobile = n.Units[PlayerSide.A].MobileCount,
                PlayerAGarrison = n.Units[PlayerSide.A].GarrisonCount,
                PlayerBMobile = n.Units[PlayerSide.B].MobileCount,
                PlayerBGarrison = n.Units[PlayerSide.B].GarrisonCount
            }
        );

        // 7단계: 정리 및 라운드 증가
        foreach (var node in Nodes.Values)
            node.ClearDepartureHistory();

        // 예약된 이동 초기화
        _pendingMoves[PlayerSide.A].Clear();
        _pendingMoves[PlayerSide.B].Clear();

        int completedRound = CurrentRound;
        CurrentRound++;
        UnitUsedThisRound[PlayerSide.A] = 0;
        UnitUsedThisRound[PlayerSide.B] = 0;

        // 8단계: 단일 RoundResolved 이벤트 발행
        RaiseEvent(new RoundResolved(
            RoomId,
            completedRound,
            moveExecutions,
            arrivalRecords,
            encounterRecords,
            ownershipChanges,
            resolvedEncounters,
            nodeSnapshots
        ));

        // 9단계: 게임 종료 체크
        if (CheckGameOver() || CurrentRound > MaxRounds)
        {
            Phase = GamePhase.GameOver;
            var winner = GetWinner();
            var reason = CurrentRound > MaxRounds
                ? GameOverReason.MaxRoundsReached
                : GameOverReason.AllNodesCaptured;

            var scores = new Dictionary<PlayerSide, int>
            {
                { PlayerSide.A, Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA) },
                { PlayerSide.B, Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB) }
            };

            // 게임 종료 시 
            RaiseEvent(new GameOver(RoomId, winner, reason, completedRound, scores));
            result.GameOver = true;
            result.Winner = winner;
        }
        else
        {
            Phase = GamePhase.Planning;
            RaiseEvent(new RoundStarted(RoomId, CurrentRound, nextRoundDeadline ?? DateTime.UtcNow.AddSeconds(30)));
            result.GameOver = false;
        }

        return result;
    }
    // 수정된 MoveUnits 메서드
    // Planning 계획 단계에서 유닛 차감되는 문제 수정 
    public MoveResult MoveUnits(PlayerSide side, MoveCommand command)
    {
        if (Phase != GamePhase.Planning)
            throw new DomainException($"Cannot move in {Phase} phase");

        if (UnitUsedThisRound[side] >= MaxUnitsPerPlayer)
            throw new DomainException("All units already moved this round");

        // 이번 명령으로 사용할 유닛 수 (남은 유닛 수를 초과할 수 없음)
        int remainingUnits = MaxUnitsPerPlayer - UnitUsedThisRound[side];
        int actualCount = Math.Min(command.UnitCount, remainingUnits);

        if (actualCount <= 0)
            throw new DomainException("No units available to commit");

        ValidatePath(command.From, command.To);

        var sourceNode = Nodes[command.From];

        // 출발지에 실제로 유닛이 충분한지 확인 (기존 예약된 이동 수 차감)
        int alreadyCommitted = _pendingMoves[side].Where(m => m.From == command.From).Sum(m => m.Count);
        int available = sourceNode.GetMobileCount(side) - alreadyCommitted;

        if (available < actualCount)
            throw new DomainException(
                $"Not enough units at {command.From}. Available: {available}, Requested: {actualCount}");

        // 예약 확정 (취소 불가)
        _pendingMoves[side].Add(new PendingMove(command.From, command.To, actualCount));
        UnitUsedThisRound[side] += actualCount;

        // 이벤트 발행
        RaiseEvent(new UnitsDeparted(RoomId, side, command.From, actualCount, CurrentRound));

        return new MoveResult(actualCount, command.From, command.To);
    }

    // 
    public void ResolveEncounter(EdgeId edgeId, PlayerSide deciderSide, EncounterDecision decision)
    {
        if (Phase != GamePhase.Planning)
            throw new DomainException($"Encounter decisions must be made during Planning phase");

        var pending = PendingEncounters.FirstOrDefault(e => e.EdgeId == edgeId);
        if (pending == null)
            throw new DomainException($"No pending encounter on edge {edgeId}");

        // 이미 결정했는지 확인 (번복 불가)
        if (pending.HasDecided(deciderSide))
            throw new DomainException("Decision already made for this encounter and cannot be changed");

        // 결정 처리
        var edge = Edges[edgeId];
        var decidingGroup = pending.GroupA.Side == deciderSide ? pending.GroupA : pending.GroupB;

        EncounterOutcome outcome;
        if (decision == EncounterDecision.Retreat)
        {
            NodeId retreatDestination = edge.Id.GetOppositeNode(decidingGroup.Destination);
            edge.RemoveTravelingGroup(deciderSide);
            edge.StartTravel(deciderSide, decidingGroup.UnitCount, retreatDestination);

            outcome = new EncounterOutcome(
                deciderSide,
                retreatDestination,
                decidingGroup.UnitCount,
                edge.Distance.RoundsRequired
            );

            RaiseEvent(new UnitsRetreated(RoomId, deciderSide, edgeId, decidingGroup.UnitCount, CurrentRound));
        }
        else
        {
            outcome = new EncounterOutcome(
                deciderSide,
                decidingGroup.Destination,
                decidingGroup.UnitCount,
                pending.RemainingRounds
            );

            RaiseEvent(new UnitsAdvanced(RoomId, deciderSide, edgeId, decidingGroup.UnitCount, CurrentRound));
        }

        // 결정 확정 (변경 불가)
        pending.MarkDecided(deciderSide, decision, outcome);

        // 비공개 이벤트
        RaiseEvent(new EncounterDecisionMade(
            RoomId, deciderSide, edgeId, decision, decidingGroup.UnitCount, CurrentRound
        ));

        // 양측 모두 결정 완료
        if (pending.BothDecided)
        {
            PendingEncounters.Remove(pending);
            RaiseEvent(new EncounterResolved(
                RoomId, edgeId,
                pending.DecisionA!.Value, pending.DecisionB!.Value,
                pending.OutcomeA!, pending.OutcomeB!,
                CurrentRound
            ));
        }
    }

    // 미사용 메서드 추후 삭제 여부 결정 
    private NodeId GetRetreatDestination(Edge edge, PlayerSide side)
    {
        // 가장 먼저 도착한 이동 그룹을 찾음
        // travelingUnits의 키값은 라운드 수
        // 가장 작은 키값을 가진 그룹이 가장 먼저 도착
        // Key 라운드 순으로 자동 정렬되는 자료형 사용 (SortedList)
        var group = edge.TravelingUnits
            .SelectMany(kvp => kvp.Value)
            .FirstOrDefault(g => g.Side == side);

        if (group == null)
            throw new InvalidOperationException($"No traveling group found for {side} on edge {edge.Id}");

        // 목적지의 반대편 노드가 출발지
        return edge.Id.GetOppositeNode(group.Destination);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
        _encounterEvents.Clear();
        _ownershipChanges.Clear();
        _arrivalRecords.Clear();
    }
}