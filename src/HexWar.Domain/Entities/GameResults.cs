namespace HexWar.Domain.Entities;

using HexWar.Domain.Enums;
using HexWar.Domain.Events;
using HexWar.Domain.ValueObjects;
using System.Text.Json.Serialization;

public record MoveResult(int ActualMoved, NodeId From, NodeId To);

public record ArrivalInfo(NodeId Destination, PlayerSide Side, int Count, EdgeId ViaEdge);

public class RoundResolutionResult
{
    public List<ArrivalInfo> ArrivedUnits { get; } = new();
    public List<EncounterOccurred> Encounters { get; } = new();
    public List<PendingEncounter> PendingEncounters { get; } = new();
    public bool GameOver { get; set; }
    public PlayerSide? Winner { get; set; }
}


public class PendingEncounter
{
    [JsonInclude]
    public EdgeId EdgeId { get; private set; }

    [JsonInclude]
    public TravelingGroup GroupA { get; private set; }

    [JsonInclude]
    public TravelingGroup GroupB { get; private set; }

    [JsonInclude]
    public int RemainingRounds { get; private set; }

    [JsonInclude]
    public EncounterDecision? DecisionA { get; private set; }

    [JsonInclude]
    public EncounterDecision? DecisionB { get; private set; }

    [JsonInclude]
    public EncounterOutcome? OutcomeA { get; private set; }

    [JsonInclude]
    public EncounterOutcome? OutcomeB { get; private set; }

    public bool BothDecided => DecisionA.HasValue && DecisionB.HasValue;

    [JsonConstructor]
    public PendingEncounter(EdgeId edgeId, TravelingGroup groupA, TravelingGroup groupB, int remainingRounds)
    {
        EdgeId = edgeId;
        GroupA = groupA;
        GroupB = groupB;
        RemainingRounds = remainingRounds;
    }

    public void MarkDecided(PlayerSide side, EncounterDecision decision, EncounterOutcome outcome)
    {
        if (side == PlayerSide.A)
        {
            DecisionA = decision;
            OutcomeA = outcome;
        }
        else
        {
            DecisionB = decision;
            OutcomeB = outcome;
        }
    }

    public bool HasDecided(PlayerSide side) => side == PlayerSide.A ? DecisionA.HasValue : DecisionB.HasValue;

    public EncounterDecision? GetDecision(PlayerSide side) => side == PlayerSide.A ? DecisionA : DecisionB;
}

/*
[PendingEncounter on Edge N1↔N4]

1. Player A 결정: Advance
   → MarkDecided(PlayerA, Advance, outcomeA)
   → BothDecided = false (B 결정 기다림)
   → Events: EncounterDecisionMade(PlayerA, Advance)

2. Player B 결정: Retreat
   → MarkDecided(PlayerB, Retreat, outcomeB)
   → BothDecided = true → 해소!
   → Events: EncounterDecisionMade(PlayerB, Retreat)
   → Events: EncounterResolved(결과 요약)
   → PendingEncounters.Remove(pending)
      */