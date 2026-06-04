namespace HexWar.Domain.Entities;

using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;

// 이동중인 유닛 그룹 정보
// 전장 -> 간선 -> 도착 노드 로 이동
// 간선에서 이동중인 유닛들을 관리 하는 객체
public class TravelingGroup
{
    // 유닛 소유권
    public PlayerSide Side { get; }
    // 유닛 수량
    public int UnitCount { get; }
    // 도착 노드
    public NodeId Destination { get; }

    public TravelingGroup(PlayerSide side, int unitCount, NodeId destination)
    {
        Side = side;
        UnitCount = unitCount;
        Destination = destination;
    }

    // 외부 공개용 정보 생성 
    public TravelingGroupInfo ToPublicInfo()
    {
        // 목적지는 비공개
        return new TravelingGroupInfo(Side, UnitCount);
    }
}

public record TravelingGroupInfo(PlayerSide Side, int UnitCount);