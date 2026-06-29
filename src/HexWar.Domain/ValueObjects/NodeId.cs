using ProtoBuf;

namespace HexWar.Domain.ValueObjects;

// 값 객채로 선언하여 불변성 유지 
[ProtoContract]
public readonly record struct NodeId([property: ProtoMember(1)] int Value)
{
    public override string ToString() => $"N{Value}";
}