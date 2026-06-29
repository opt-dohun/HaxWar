using HexWar.Domain.Enums;
using ProtoBuf;

namespace HexWar.Domain.ValueObjects;

[ProtoContract]
public readonly record struct EdgeId
{
    [ProtoMember(1)]
    public NodeId From { get; }

    [ProtoMember(2)]
    public NodeId To { get; }


    public EdgeId(NodeId from, NodeId to)
    {
        if (from.Value < to.Value)
        {
            From = from;
            To = to;
        }
        else
        {
            From = to;
            To = from;
        }
    }

    public string Key => $"{From.Value}-{To.Value}";

    public override string ToString() => Key;

    // 주어진 노드의 반대편 노드를 반환
    public NodeId GetOppositeNode(NodeId nodeId)
    {
        if (nodeId == From) return To;
        if (nodeId == To) return From;
        throw new ArgumentException($"Node {nodeId} is not part of edge {this}");
    }
}