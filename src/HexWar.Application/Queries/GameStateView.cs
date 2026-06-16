// src/HexWar.Application/Queries/GameStateView.cs
namespace HexWar.Application.Queries;

public class GameStateView
{
    public string RoomId { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public int CurrentRound { get; init; }
    public int MaxRounds { get; init; }
    public string MySide { get; init; } = string.Empty;

    // Planning 상태
    public int MyRemainingUnits { get; init; }
    public List<PendingMoveView> MyPendingMoves { get; init; } = new();
    public DateTime? Deadline { get; set; }
    public bool MoveCommandCompleted { get; init; }
    public bool EncounterDecisionsCompleted { get; init; }
    public bool IsMyPlanningComplete { get; init; }

    // 게임 요소
    public List<NodeView> Nodes { get; init; } = new();
    public List<EdgeView> Edges { get; init; } = new();
    public List<PendingEncounterDetailView> PendingEncounters { get; init; } = new();
    public List<string> UndecidedEncounterEdgeIds { get; init; } = new();

    // 게임 종료
    public bool IsGameOver { get; init; }
    public string? Winner { get; init; }
    public Dictionary<string, int> Scores { get; init; } = new();
}

public class PendingMoveView
{
    public int FromNodeId { get; init; }
    public int ToNodeId { get; init; }
    public int UnitCount { get; init; }
}

public class NodeView
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Ownership { get; init; } = string.Empty;
    public bool IsHeadquarters { get; init; }
    public bool IsSupplyLine { get; init; }
    public List<int> Neighbors { get; init; } = new();
    public bool IsOwnedByMe { get; init; }

    public UnitGroupDetailView MyUnits { get; init; } = new();
    public EnemyUnitDetailView EnemyUnits { get; init; } = new();
    public List<DepartureView> RecentDepartures { get; init; } = new();
}

public class UnitGroupDetailView
{
    public int Mobile { get; init; }
    public int Garrison { get; init; }
    public int Total { get; init; }
}

public class EnemyUnitDetailView
{
    public int TotalCount { get; init; }
    public int? MobileCount { get; init; }
    public int? GarrisonCount { get; init; }
    public bool IsFullVisibility { get; init; }
}

public class DepartureView
{
    public int FromNodeId { get; init; }
    public int UnitCount { get; init; }
}

public class EdgeView
{
    public int FromNodeId { get; init; }
    public int ToNodeId { get; init; }
    public int Distance { get; init; }
    public bool HasMyUnitsTraveling { get; init; }
    public bool? HasEnemyUnitsTraveling { get; init; }
    public bool HasEncounter { get; init; }
    public List<TravelingUnitView> MyTravelingUnits { get; init; } = new();
}

public class TravelingUnitView
{
    public int UnitCount { get; init; }
    public int DestinationNodeId { get; init; }
    public int RemainingRounds { get; init; }
}

public class PendingEncounterDetailView
{
    public string EdgeId { get; init; } = string.Empty;
    public int FromNodeId { get; init; }
    public int ToNodeId { get; init; }
    public int MyUnitCount { get; init; }
    public int MyDestinationNodeId { get; init; }
    public int EnemyUnitCount { get; init; }
    public int RemainingRounds { get; init; }
    public bool IHaveDecided { get; init; }
    public string? MyDecision { get; init; }
    public bool IsResolved { get; init; }
    public string? EnemyDecision { get; init; }
}