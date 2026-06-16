namespace HexWar.Domain.Entities;

using System.Text.Json.Serialization;
using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;

public class Node
{
    [JsonInclude]
    public NodeId Id { get; private set; }

    [JsonInclude]
    public string Name { get; private set; }

    [JsonInclude]
    public NodeType Type { get; private set; } // 전초기지 유형 표현 추가
    public bool IsHeadquarters => Type == NodeType.Headquarters;
    public bool IsSupplyLine => Type == NodeType.SupplyLine;

    // 점유 상태
    [JsonInclude]
    public NodeOwnership Ownership { get; set; }

    // 각 플레이어별 유닛 그룹
    [JsonInclude]
    public Dictionary<PlayerSide, UnitGroup> Units { get; private set; } = new()
    {
        { PlayerSide.A , new UnitGroup(PlayerSide.A)  },
        { PlayerSide.B , new UnitGroup(PlayerSide.B) }
    };

    // 이웃한 노드 정보 List
    [JsonInclude]
    public List<NodeId> Neighbors { get; private set; } = new();

    // 이번 라운드 출발 정보 (상대방에게 공개될 리스트)
    [JsonInclude]
    public List<DepartureAnnouncement> RecentDepartures { get; private set; } = new();

    [JsonConstructor]
    public Node(NodeId id, string name, NodeType type = NodeType.OutPost)
    {
        Id = id;
        Name = name;
        Type = type;
        Ownership = NodeOwnership.Neutral;
    }
    public int GetMobileCount(PlayerSide side) => Units[side].MobileCount;
    public int GetTotalCount(PlayerSide side) => Units[side].TotalCount;

    // 유닛 도착 처리 
    public void ArriveMobileUnits(PlayerSide side, int count)
    {
        // 0 이상의 값만 통과할 수 있도록 검증
        if (count <= 0) throw new ArgumentException("Unit count must be positive");
        Units[side].AddMobile(count);
        UpdateOwnership();
        ActivateSupplyLineIfNeeded();
    }

    // 유닛 출발 처리
    public int DepartMobileUnits(PlayerSide side, int requested)
    {
        // 플레이어 사용가능 병력 
        int available = GetMobileCount(side);
        int actual = Math.Min(requested, available);

        // 이동 병력이 충분한지 검사
        if (actual <= 0)
            throw new InvalidOperationException($"No mobile units at node {Id} for {side}");

        Units[side].RemoveMobile(actual);
        RecentDepartures.Add(new DepartureAnnouncement(Id, actual));
        UpdateOwnership();

        return actual;
    }

    // 보급로 활성화 확인
    private void ActivateSupplyLineIfNeeded()
    {
        if (!IsSupplyLine) return;

        // 점유 중인 플레이어 확인
        if (Ownership == NodeOwnership.PlayerA)
            Units[PlayerSide.A].SetGarrison(1);
        else if (Ownership == NodeOwnership.PlayerB)
            Units[PlayerSide.B].SetGarrison(1);
    }

    // 보급로 상실 확인
    private void DeactivateSupplyLine()
    {
        if (!IsSupplyLine) return;

        if (Ownership != NodeOwnership.PlayerA)
            Units[PlayerSide.A].SetGarrison(0);
        if (Ownership != NodeOwnership.PlayerB)
            Units[PlayerSide.B].SetGarrison(0);
    }

    public void UpdateOwnership()
    {
        // 분부 여부 확인 및 조기 탈출
        if (IsHeadquarters)
        {
            Ownership = NodeOwnership.Neutral; // 본부는 항상 중립
            return;
        }

        int countA = GetTotalCount(PlayerSide.A);
        int countB = GetTotalCount(PlayerSide.B);

        NodeOwnership previousOwnership = Ownership;

        if (countA == 0 && countB == 0)
        {
            Ownership = NodeOwnership.Neutral;
        }
        else if (countA > countB)
        {
            Ownership = NodeOwnership.PlayerA;
        }
        else if (countA < countB)
        {
            Ownership = NodeOwnership.PlayerB;
        }
        else
        {
            Ownership = NodeOwnership.Contested;
        }

        if (previousOwnership != Ownership)
        {
            DeactivateSupplyLine();
            ActivateSupplyLineIfNeeded();
        }

    }

    // 라운드 내 출발 기록 초기화 
    public void ClearDepartureHistory()
    {
        RecentDepartures.Clear();
    }
}

public record DepartureAnnouncement(NodeId FromNode, int UnitCount);