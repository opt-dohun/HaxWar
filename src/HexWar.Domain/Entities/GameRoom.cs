namespace HexWar.Domain.Entities;

using System.Text.Json.Serialization;
using HexWar.Domain.Commands;
using HexWar.Domain.Enums;
using HexWar.Domain.Events;
using HexWar.Domain.Exceptions;
using HexWar.Domain.ValueObjects;

public partial class GameRoom
{
    [JsonInclude]
    public string RoomId { get; private set; }

    [JsonInclude]
    public string? OwnerServerId { get; private set; }

    public void AssignOwner(string serverId)
    {
        OwnerServerId = serverId;
    }

    [JsonInclude]
    public GamePhase Phase { get; private set; }

    [JsonInclude]
    public int CurrentRound { get; private set; }

    [JsonInclude]
    public int MaxRounds { get; private set; } = 20;

    // 순차적 접근이 필요하지 않음으로 index 기반의 리스트보다 Dictionary가 더 적합함
    [JsonInclude]
    public Dictionary<NodeId, Node> Nodes { get; private set; } = new();

    [JsonInclude]
    public Dictionary<EdgeId, Edge> Edges { get; private set; } = new();

    [JsonInclude]
    public Dictionary<PlayerSide, PlayerId> Players { get; private set; } = new();

    // 라운드에 소모한 유닛 수 파악용
    [JsonInclude]
    public Dictionary<PlayerSide, int> UnitUsedThisRound { get; private set; } = new()
    {
        { PlayerSide.A, 0 },
        { PlayerSide.B, 0 }
    };

    // Planning 단계에서 예약된 이동 명령 (취소 불가)
    [JsonInclude]
    [JsonPropertyName("pendingMoves")]
    private Dictionary<PlayerSide, List<PendingMove>> _pendingMoves = new()
    {
        { PlayerSide.A, new List<PendingMove>() },
        { PlayerSide.B, new List<PendingMove>() }
    };

    // 최대 이동 가능한 유닛 수
    public const int MaxUnitsPerPlayer = 3;



    // 조우 이벤트 목록
    [JsonInclude]
    public List<PendingEncounter> PendingEncounters { get; private set; } = new();

    // 조우 결정 완료 여부
    [JsonInclude]
    public Dictionary<PlayerSide, bool> EncounterDecisionReady { get; private set; } = new()
    {
        { PlayerSide.A, false },
        { PlayerSide.B, false }
    };

    // 생성자 함수를 통한 기본 상태 정의
    [JsonConstructor]
    public GameRoom(string roomId)
    {
        RoomId = roomId;
        Phase = GamePhase.WatingForPlayers;
    }


    // 게임 설정 단계 정의
    public void InitializeMap()
    {
        // 노드 생성 (6개 노드)
        CreateNode(new NodeId(1), "서부 전초기지", NodeType.OutPost);
        CreateNode(new NodeId(2), "북부 고지", NodeType.Chokepoint);
        CreateNode(new NodeId(3), "동부 교차로", NodeType.SupplyLine);  // 보급로!
        CreateNode(new NodeId(4), "남부 통로", NodeType.Chokepoint);
        CreateNode(new NodeId(5), "동부 전초기지", NodeType.OutPost);
        CreateNode(new NodeId(6), "중앙 사령부", NodeType.Headquarters);

        // 간선 생성 (수정안: N4-N2 연결 제거)
        CreateEdge(new NodeId(1), new NodeId(2), new Distance(1));  // 서부-북부
        CreateEdge(new NodeId(1), new NodeId(4), new Distance(2));  // 서부-남부 (우회로)
        CreateEdge(new NodeId(1), new NodeId(5), new Distance(2));  // 서부-동부전초
        CreateEdge(new NodeId(1), new NodeId(6), new Distance(1));  // 서부-본부

        CreateEdge(new NodeId(2), new NodeId(3), new Distance(2));  // 북부-동부교차로
        CreateEdge(new NodeId(2), new NodeId(6), new Distance(1));  // 북부-본부

        CreateEdge(new NodeId(3), new NodeId(4), new Distance(1));  // 동부교차로-남부
        CreateEdge(new NodeId(3), new NodeId(5), new Distance(1));  // 동부교차로-동부전초
        CreateEdge(new NodeId(3), new NodeId(6), new Distance(1));  // 동부교차로-본부

        CreateEdge(new NodeId(4), new NodeId(5), new Distance(1));  // 남부-동부전초
        CreateEdge(new NodeId(4), new NodeId(6), new Distance(1));  // 남부-본부

        CreateEdge(new NodeId(5), new NodeId(6), new Distance(1));  // 동부전초-본부
    }

    public void CreateNode(NodeId id, string name, NodeType type)
    {
        // 이미 존재하는 노드인지 검사
        if (Nodes.ContainsKey(id))
            throw new InvalidOperationException($"Node with id {id} already exists in the game room.");

        var node = new Node(id, name, type);
        Nodes[id] = node;

    }

    public void CreateEdge(NodeId from, NodeId to, Distance distance)
    {
        var edge = new Edge(from, to, distance);
        Edges[edge.Id] = edge;

        // 이웃 정보 등록
        Nodes[from].Neighbors.Add(to);
        Nodes[to].Neighbors.Add(from);
    }

    // 플레이어 참가 
    public PlayerSide AddPlayer(PlayerId playerId, DateTime? gameStartDeadline = null)
    {
        if (Players.Count >= 2)
        {
            throw new DomainException("Room is full");
        }

        if (Players.ContainsValue(playerId))
        {
            throw new DomainException("Player already in room");
        }

        // 세션 내 플레이어 정보 추가
        PlayerSide side = Players.Count == 0 ? PlayerSide.A : PlayerSide.B;
        Players[side] = playerId;

        // 유닛 배치 초기화
        // Player A → Node 1, Player B → Node 5
        NodeId startNode = side == PlayerSide.A ? new NodeId(1) : new NodeId(5);
        Nodes[startNode].Units[side].AddMobile(3);
        Nodes[startNode].Ownership = side == PlayerSide.A ? NodeOwnership.PlayerA : NodeOwnership.PlayerB;


        // 플레이 모집 여부 확인
        if (Players.Count >= 2)
        {
            Phase = GamePhase.Planning;
            CurrentRound = 1;
            // 이벤트 발생 목록에 추가 
            RaiseEvent(new GameStarted(RoomId, Players[PlayerSide.A], Players[PlayerSide.B], gameStartDeadline ?? DateTime.UtcNow.AddSeconds(30)));
        }

        return side;
    }


    private void ValidatePath(NodeId from, NodeId to)
    {
        var edgeId = new EdgeId(from, to);
        if (!Edges.ContainsKey(edgeId))
        {
            throw new DomainException($"No path between {from} and {to}");
        }

    }



    private List<ArrivalInfo> ProcessAllEdgeAdvances()
    {
        var arrivals = new List<ArrivalInfo>();

        foreach (var edge in Edges.Values)
        {
            var arrived = edge.AdvanceRound();
            foreach (var group in arrived)
            {
                arrivals.Add(new ArrivalInfo(
                    group.Destination, group.Side, group.UnitCount, edge.Id
                ));
            }
        }

        return arrivals;
    }

    private List<EncounterInfo> FindAllEncounters()
    {
        return Edges.Values
            .SelectMany(e => e.FindAllEncounters())
            .ToList();
    }

    public List<ArrivalInfo> GetBlockedUnits(List<EncounterInfo> encounters)
    {
        var blocked = new List<ArrivalInfo>();

        foreach (var enc in encounters)
        {
            blocked.Add(new ArrivalInfo(enc.GroupA.Destination, enc.GroupA.Side, enc.GroupA.UnitCount, enc.EdgeId));
            blocked.Add(new ArrivalInfo(enc.GroupB.Destination, enc.GroupB.Side, enc.GroupB.UnitCount, enc.EdgeId));
        }

        return blocked;
    }



    // --- 승리 조건 ---

    private bool CheckGameOver()
    {
        // 본부 제외 모든 노드가 한 플레이어 소유인지 확인
        var nonHQNodes = Nodes.Values.Where(n => !n.IsHeadquarters).ToList();
        bool allPlayerA = nonHQNodes.All(n => n.Ownership == NodeOwnership.PlayerA);
        bool allPlayerB = nonHQNodes.All(n => n.Ownership == NodeOwnership.PlayerB);
        return allPlayerA || allPlayerB;
    }

    private PlayerSide? GetWinner()
    {
        int nodesA = Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA);
        int nodesB = Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB);

        if (nodesA > nodesB) return PlayerSide.A;
        if (nodesB > nodesA) return PlayerSide.B;
        return null; // 무승부
    }

    // Planning 단계 내에 주요 내용 처리 여부
    public bool IsPlanningComplete
    {
        get
        {
            if (Phase != GamePhase.Planning) return false;

            if (PendingEncounters.Any())
            {
                bool allEncounterResolve = EncounterDecisionReady[PlayerSide.A] && EncounterDecisionReady[PlayerSide.B];
                bool allUnitUsed = UnitUsedThisRound[PlayerSide.A] >= MaxUnitsPerPlayer && UnitUsedThisRound[PlayerSide.B] >= MaxUnitsPerPlayer;

                return allEncounterResolve && allUnitUsed;
            }

            return UnitUsedThisRound[PlayerSide.A] >= MaxUnitsPerPlayer && UnitUsedThisRound[PlayerSide.B] >= MaxUnitsPerPlayer;
        }

    }

    // --- 규칙 검증 메서드 --- 애플리케이션 계층에서 검증을 위해 단독 호출할 수 있음
    public bool CanMoveUnits(PlayerSide side)
    {
        return Phase == GamePhase.Planning && UnitUsedThisRound[side] < MaxUnitsPerPlayer;
    }

    public int GetAvailableUnits(PlayerSide side)
    {
        if (Phase != GamePhase.Planning) return 0;
        return MaxUnitsPerPlayer - UnitUsedThisRound[side];
    }

    public bool HasPendingEncounterOn(EdgeId edgeId)
    {
        return PendingEncounters.Any(p => p.EdgeId == edgeId);
    }

    public List<PendingEncounter> GetUndecidedEncounters(PlayerSide side)
    {
        return PendingEncounters
        .Where(e => !e.HasDecided(side))
        .ToList();
    }

    public bool IsReadyForResolution()
    {
        if (Phase != GamePhase.Planning) return false;
        bool allEncountersResolved = PendingEncounters.All(e => e.BothDecided);
        return allEncountersResolved;
    }

    public int GetRemainingUnits(PlayerSide side)
    {
        return MaxUnitsPerPlayer - UnitUsedThisRound[side];
    }

    public IReadOnlyList<PendingMove> GetPendingMoves(PlayerSide side)
    {
        return _pendingMoves[side].AsReadOnly();
    }

    /// <summary>
    /// 누적된 도메인 이벤트를 반환하고 내부 리스트를 비웁니다.
    /// </summary>
    public IReadOnlyList<IDomainEvent> FlushDomainEvents()
    {
        var events = _domainEvents.ToList();
        ClearDomainEvents();
        return events;
    }

    /// <summary>
    /// 외부 요인(예: 접속 종료)으로 게임을 즉시 종료시킵니다.
    /// </summary>
    public void ForceGameOver(PlayerSide? winner, GameOverReason reason)
    {
        Phase = GamePhase.GameOver;
        var scores = new Dictionary<PlayerSide, int>
        {
            { PlayerSide.A, Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerA) },
            { PlayerSide.B, Nodes.Values.Count(n => n.Ownership == NodeOwnership.PlayerB) }
        };
        RaiseEvent(new GameOver(RoomId, winner, reason, CurrentRound, scores));
    }
}

public record PendingMove(NodeId From, NodeId To, int Count);