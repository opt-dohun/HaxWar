using System.Collections.Generic;
using HexWar.Domain.Entities;
using HexWar.Domain.Enums;
using HexWar.Domain.ValueObjects;
using HexWar.Domain.Commands;
using Xunit;

namespace HexWar.Domain.Tests;

public class GameRoomTests
{
    private readonly GameRoom _room;

    public GameRoomTests()
    {
        _room = new GameRoom("test-room");
        _room.InitializeMap();
    }

    [Fact]
    public void AddPlayer_ShouldSetInitialUnitsAtStartNode()
    {
        var side = _room.AddPlayer(new PlayerId("player1"));

        Assert.Equal(PlayerSide.A, side);
        Assert.Equal(3, _room.Nodes[new NodeId(1)].StationedUnits[PlayerSide.A]);
        Assert.Equal(NodeOwnership.PlayerA, _room.Nodes[new NodeId(1)].Ownership);
    }

    [Fact]
    public void MoveUnits_ShouldDeductFromSourceAndTravel()
    {
        _room.AddPlayer(new PlayerId("p1"));
        _room.AddPlayer(new PlayerId("p2"));
        // 현재 Phase: Planning

        var cmd = new MoveCommand(new NodeId(1), new NodeId(2), new List<int> { 1, 2 });
        _room.MoveUnits(PlayerSide.A, cmd);

        Assert.Equal(1, _room.Nodes[new NodeId(1)].StationedUnits[PlayerSide.A]);
        Assert.Equal(2, _room.UnitUsedThisRound[PlayerSide.A]);

        var edgeId = new EdgeId(new NodeId(1), new NodeId(2));
        Assert.NotEmpty(_room.Edges[edgeId].TravelingUnits);
    }

    [Fact]
    public void SameNode_EqualUnits_ShouldBeContested()
    {
        var node = _room.Nodes[new NodeId(2)];
        node.ArriveUnits(PlayerSide.A, 2);
        node.ArriveUnits(PlayerSide.B, 2);

        Assert.Equal(NodeOwnership.Contested, node.Ownership);
    }

    [Fact]
    public void Headquarters_ShouldAlwaysBeNeutral()
    {
        var hq = _room.Nodes[new NodeId(6)];
        hq.ArriveUnits(PlayerSide.A, 10);

        Assert.Equal(NodeOwnership.Neutral, hq.Ownership);
    }
}