namespace HexWar.Domain.ValueObjects;

public readonly record struct Distance(int RoundsRequired)
{
    // 즉시 도달 가능 여부 확인
    public bool IsInstant => RoundsRequired == 0;

    // 비용 계산 메서드
    public Distance AddRounds(int round) => new(RoundsRequired + round);

}