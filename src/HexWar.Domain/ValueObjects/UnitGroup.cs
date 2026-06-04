namespace HexWar.Domain.ValueObjects;

using HexWar.Domain.Enums;

// 노드에 주둔 중인 유닛 그룹을 표현
public class UnitGroup
{
    public PlayerSide Side { get; }
    public int MobileCount { get; private set; }
    public int GarrisonCount { get; private set; } // 고정 수비대

    public int TotalCount => MobileCount + GarrisonCount;

    public UnitGroup(PlayerSide side, int mobileCount = 0, int garrisonCount = 0)
    {
        Side = side;
        MobileCount = mobileCount;
        GarrisonCount = garrisonCount;
    }

    public void AddMobile(int count)
    {
        if (count < 0) throw new ArgumentException("Count cannot be negative");
        MobileCount += count;
    }

    public void RemoveMobile(int count)
    {
        // 이동시킬 수 있는 유닛 확인 
        if (count > MobileCount)
            throw new InvalidOperationException($"Not enough mobile units. Have: {MobileCount}, Remove: {count}");
        MobileCount -= count;
    }

    public void SetGarrison(int count)
    {
        GarrisonCount = Math.Max(0, count);
    }

    public void Clear()
    {
        MobileCount = 0;
        GarrisonCount = 0;
    }
}