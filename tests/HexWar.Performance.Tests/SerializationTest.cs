namespace HexWar.Performance.Tests;

using System;
using System.Linq;
using System.Text.Json;
using HexWar.Domain.Commands;
using HexWar.Domain.Entities;
using HexWar.Domain.Enums;
using HexWar.Domain.Events;
using HexWar.Domain.ValueObjects;
using HexWar.Infrastructure.Serialization;
using NUnit.Framework;

[TestFixture]
public class SerializationTest
{
    [Test]
    public void GameRoom_Serialization_Verification()
    {
        // 1. Arrange: Create and set up GameRoom state
        var roomId = "test-room-123";
        var room = new GameRoom(roomId);
        room.InitializeMap();

        var playerAId = new PlayerId("player-A");
        var playerBId = new PlayerId("player-B");

        room.AddPlayer(playerAId);
        room.AddPlayer(playerBId); // GamePhase transitions to Planning

        // Player A moves 2 units from Node 1 to Node 4 (distance 2)
        room.MoveUnits(PlayerSide.A, new MoveCommand(new NodeId(1), new NodeId(4), 2));
        // Player B moves 2 units from Node 5 to Node 3 (distance 1)
        room.MoveUnits(PlayerSide.B, new MoveCommand(new NodeId(5), new NodeId(3), 2));

        // Let's resolve round to put units in travel state
        room.ResolveRound();

        // Now we should have traveling units on edges!
        // Let's add some pending encounters if they exist or simulate one.
        // Let's check if there's any pending encounter. Since both moved, let's see.
        // We can manually add a pending encounter to the room.PendingEncounters to test its serialization.
        var edgeId = new EdgeId(new NodeId(1), new NodeId(2));
        var groupA = new TravelingGroup(PlayerSide.A, 2, new NodeId(2));
        var groupB = new TravelingGroup(PlayerSide.B, 1, new NodeId(1));
        var encounter = new PendingEncounter(edgeId, groupA, groupB, 1);
        encounter.MarkDecided(PlayerSide.A, EncounterDecision.Advance, new EncounterOutcome(PlayerSide.A, new NodeId(2), 2, 1));
        room.PendingEncounters.Add(encounter);

        var jsonOptions = DomainJsonOptions.Create();

        // 2. Act: Serialize
        string json = JsonSerializer.Serialize(room, jsonOptions);

        // 3. Act: Deserialize
        GameRoom? deserializedRoom = null;
        Exception? deserializeEx = null;
        try
        {
            deserializedRoom = JsonSerializer.Deserialize<GameRoom>(json, jsonOptions);
        }
        catch (Exception ex)
        {
            deserializeEx = ex;
        }

        // 4. Assert
        Assert.That(deserializeEx, Is.Null, $"Deserialization failed with exception: {deserializeEx}");
        Assert.That(deserializedRoom, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(deserializedRoom!.RoomId, Is.EqualTo(room.RoomId), "RoomId mismatch");
            Assert.That(deserializedRoom.Phase, Is.EqualTo(room.Phase), "Phase mismatch");
            Assert.That(deserializedRoom.CurrentRound, Is.EqualTo(room.CurrentRound), "CurrentRound mismatch");

            // Players
            Assert.That(deserializedRoom.Players.ContainsKey(PlayerSide.A), Is.True, "Player A key missing");
            Assert.That(deserializedRoom.Players[PlayerSide.A], Is.EqualTo(playerAId), "Player A value mismatch");

            // UnitUsedThisRound
            Assert.That(deserializedRoom.UnitUsedThisRound[PlayerSide.A], Is.EqualTo(room.UnitUsedThisRound[PlayerSide.A]), "UnitUsedThisRound mismatch");

            // Nodes
            Assert.That(deserializedRoom.Nodes.Count, Is.EqualTo(room.Nodes.Count), "Node count mismatch");
            var node1 = deserializedRoom.Nodes[new NodeId(1)];
            Assert.That(node1.Units[PlayerSide.A].MobileCount, Is.EqualTo(room.Nodes[new NodeId(1)].Units[PlayerSide.A].MobileCount), "Node units mobile count mismatch");

            // Edges and Traveling Units
            Assert.That(deserializedRoom.Edges.Count, Is.EqualTo(room.Edges.Count), "Edge count mismatch");
            var serializedEdge = room.Edges.Values.First(e => e.TravelingUnits.Any());
            var deserializedEdge = deserializedRoom.Edges[serializedEdge.Id];
            Assert.That(deserializedEdge.TravelingUnits.Count, Is.EqualTo(serializedEdge.TravelingUnits.Count), "TravelingUnits count mismatch on edge");

            // Pending moves
            Assert.That(deserializedRoom.GetPendingMoves(PlayerSide.A).Count, Is.EqualTo(room.GetPendingMoves(PlayerSide.A).Count), "Pending moves count mismatch");

            // Pending encounters
            Assert.That(deserializedRoom.PendingEncounters.Count, Is.EqualTo(room.PendingEncounters.Count), "Pending encounters count mismatch");
            if (room.PendingEncounters.Count > 0)
            {
                var decPE = deserializedRoom.PendingEncounters[0];
                var srcPE = room.PendingEncounters[0];
                Assert.That(decPE.EdgeId, Is.EqualTo(srcPE.EdgeId), "PendingEncounter EdgeId mismatch");
                Assert.That(decPE.DecisionA, Is.EqualTo(srcPE.DecisionA), "PendingEncounter DecisionA mismatch");
                Assert.That(decPE.OutcomeA?.DestinationNode, Is.EqualTo(srcPE.OutcomeA?.DestinationNode), "PendingEncounter OutcomeA DestinationNode mismatch");
            }
        });
    }
}
