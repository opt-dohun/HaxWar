namespace HexWar.Domain.Entities;

using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;

public class Edge
{
    public EdgeId Id { get; }
    public NodeId From { get; }
    public NodeId To { get; }
    public Distance Distance { get; }

    // 현재 간선 내 이동 중인 유닛 배치 정보
    // Key : 남은 라운드 수 Value 이동 정보
    // 라운드 별로 빠른 접근이 가능하고, 정렬되어있어 순회하기 용이함으로 해당 컬렉션 객체 선택 
    public SortedList<int, List<TravelingGroup>> TravelingUnits { get; } = new();

    public Edge(NodeId from, NodeId to, Distance distance)
    {
        Id = new EdgeId(from, to);
        From = from;
        To = to;
        Distance = distance;
    }

    // 유닛 이동 시작 시작
    public void StartTravel(PlayerSide side, int unitCount, NodeId destination)
    {

        int remainingRounds = Distance.RoundsRequired;

        // 잔여 라운드 정보가 없는 경우 추가
        if (!TravelingUnits.ContainsKey(remainingRounds))
        {
            TravelingUnits.Add(remainingRounds, new List<TravelingGroup>());
        }

        // 잔여 라운드 정보에 등록 
        TravelingUnits[remainingRounds].Add(new TravelingGroup(side, unitCount, destination));
    }

    // 라운드 경과 처리에 따른 
    public List<TravelingGroup> AdvanceRound()
    {
        var arrived = new List<TravelingGroup>();
        // SortedList 사용 이유는 새로운 이동 정보를 그래도 복사하기 위하여 
        var newTraveling = new SortedList<int, List<TravelingGroup>>();

        foreach (var kvp in TravelingUnits)
        {
            int newRemaining = kvp.Key - 1;

            if (newRemaining <= 0)
            {
                arrived.AddRange(kvp.Value);
            }
            else
            {
                if (!newTraveling.ContainsKey(newRemaining))
                    newTraveling[newRemaining] = new List<TravelingGroup>();
                newTraveling[newRemaining].AddRange(kvp.Value);
            }
        }

        TravelingUnits.Clear();
        foreach (var kvp in newTraveling)
        {
            TravelingUnits[kvp.Key] = kvp.Value;
        }

        return arrived;
    }

    // 정보 가시성 
    public bool HasUnitsOfSide(PlayerSide side)
    {
        // SelectMany 메서드란 -> 1차원 리스트 안의 리스트를 1차원 리스트로 반환해주는 함수
        // TravelingUnits.Values -> [List<Group1>, List<Group2>]
        // SelectMany(g => g) -> [Group1, Group2] 

        // Any 메서드란 -> 리스트에서 조건을 만족하는 값이 하나라도 있으면 true 반환, 없으면 false 반환
        return TravelingUnits.Values
            .SelectMany(g => g).Any(g => g.Side == side);
    }

    // 조우 여부 확인
    public bool HasEncounter(int roundRemaining, out List<TravelingGroup>? groups)
    {
        // 현재 라운드 정보가 존재하는 경우
        // 1. 도착 지점이 동일한 유닛이 2개 이상인 경우
        if (TravelingUnits.TryGetValue(roundRemaining, out groups) && groups.Count > 1)
        {
            // Side 기준으로 선택하여 중복 제거된 리스트 생성
            // 1 이상이라면 다른 Side 존재 -> true
            // 1 이하라면 같은 Side 존재 -> false
            var sides = groups.Select(g => g.Side).Distinct();
            return sides.Count() > 1;
        }
        groups = null;
        return false;
    }

    // 모든 조우 찾기
    public List<EncounterInfo> FindAllEncounters()
    {
        var encounters = new List<EncounterInfo>();

        // 간선 내부 모든 유저 순환 
        foreach (var kvp in TravelingUnits)
        {
            // 도착 지점이 동일한 그룹이 2개 이상인지 확인 
            if (HasEncounter(kvp.Key, out var groups) && groups != null)
            {
                // 그룹 A, 그룹 B 찾기
                var groupA = groups.FirstOrDefault(g => g.Side == PlayerSide.A);
                var groupB = groups.FirstOrDefault(g => g.Side == PlayerSide.B);

                // 양쪽이 다 존재하는 경우 
                if (groupA != null && groupB != null)
                {
                    encounters.Add(new EncounterInfo(
                        Id,
                        groupA,
                        groupB,
                        kvp.Key
                    ));
                }
            }
        }
        return encounters;
    }

    // Any 란 -> 리스트에서 조건을 만족하는 값이 하나라도 있으면 true 반환, 없으면 false 반환 
    // () -> null 조건을 만족하는 값이 없으면 -> true
    // TravelingUnits.Any() -> 결과가 true면 !false -> false (값이 있으면 -> false) 
    // TravelingUnits.Any() -> 결과가 false면 !true -> true (값이 없으면 -> true) 
    // 즉, IsEmpty = true -> (값이 없으면), IsEmpty = false -> (값이 있으면)
    public bool IsEmpty => !TravelingUnits.Any();
}

public record EncounterInfo(EdgeId EdgeId, TravelingGroup GroupA, TravelingGroup GroupB, int RemainingRounds);