using ProtoBuf;

namespace HexWar.Domain.ValueObjects;

[ProtoContract]
public readonly record struct PlayerId([property: ProtoMember(1)] string Value)
{
    public override string ToString() => $"P:{Value}";
}