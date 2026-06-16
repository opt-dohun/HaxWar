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

    [Test]
    public void UnitsDeparted_Distributed_Serialization_Verification()
    {
        var pubOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var subOptions = HexWar.Domain.Serialization.DomainEventSerializerOptions.Create();
        var ev = new UnitsDeparted("room-123", PlayerSide.A, new NodeId(1), 3, 1);
        string json = JsonSerializer.Serialize(ev, pubOptions);
        Console.WriteLine($"Publisher JSON: {json}");
        var deserialized = JsonSerializer.Deserialize<UnitsDeparted>(json, subOptions);
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized.RoomId, Is.EqualTo("room-123"));
        Assert.That(deserialized.Side, Is.EqualTo(PlayerSide.A));
        Assert.That(deserialized.FromNode, Is.EqualTo(new NodeId(1)));
        Assert.That(deserialized.UnitCount, Is.EqualTo(3));
        Assert.That(deserialized.RoundNumber, Is.EqualTo(1));
    }

    [Test]
    public void RoundResolved_Serialization_Verification()
    {
        var pubOptions = DomainJsonOptions.Create();
        var subOptions = HexWar.Domain.Serialization.DomainEventSerializerOptions.Create();

        var arrivals = new List<ArrivalRecord>
        {
            new ArrivalRecord(new NodeId(2), PlayerSide.A, 5, new EdgeId(new NodeId(1), new NodeId(2)))
        };
        var ownershipChanges = new List<OwnershipChangeRecord>
        {
            new OwnershipChangeRecord
            {
                NodeId = 2,
                PreviousOwner = "None",
                NewOwner = "PlayerA",
                IsSupplyLineActive = true
            }
        };

        var ev = new RoundResolved(
            roomId: "room-123",
            completedRound: 1,
            moveExecutions: new List<MoveExecutionRecord>(),
            arrivals: arrivals,
            encounters: new List<EncounterRecord>(),
            ownershipChanges: ownershipChanges,
            resolvedEncounters: new List<EncounterResolvedRecord>(),
            nodeSnapshots: new Dictionary<int, NodeStateSnapshot>()
        );

        string json = JsonSerializer.Serialize(ev, pubOptions);
        Console.WriteLine($"RoundResolved JSON: {json}");

        var deserialized = JsonSerializer.Deserialize<RoundResolved>(json, subOptions);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized.RoomId, Is.EqualTo("room-123"));
        Assert.That(deserialized.CompletedRound, Is.EqualTo(1));
        Assert.That(deserialized.Arrivals, Has.Count.EqualTo(1));
        Assert.That(deserialized.Arrivals[0].DestinationNodeId.Value, Is.EqualTo(2));
        Assert.That(deserialized.Arrivals[0].Side, Is.EqualTo(PlayerSide.A));
        Assert.That(deserialized.OwnershipChanges, Has.Count.EqualTo(1));
        Assert.That(deserialized.OwnershipChanges[0].NodeId, Is.EqualTo(2));
    }

    [Test]
    public void VerifyActiveVsPassiveWebSocketSerialization()
    {
        // 1. Create a RoundResolved event in memory
        var arrivals = new List<ArrivalRecord>
        {
            new ArrivalRecord(new NodeId(2), PlayerSide.A, 5, new EdgeId(new NodeId(1), new NodeId(2)))
        };
        var ownershipChanges = new List<OwnershipChangeRecord>
        {
            new OwnershipChangeRecord
            {
                NodeId = 2,
                PreviousOwner = "None",
                NewOwner = "PlayerA",
                IsSupplyLineActive = true
            }
        };

        var domainEvent = new RoundResolved(
            roomId: "room-123",
            completedRound: 1,
            moveExecutions: new List<MoveExecutionRecord>(),
            arrivals: arrivals,
            encounters: new List<EncounterRecord>(),
            ownershipChanges: ownershipChanges,
            resolvedEncounters: new List<EncounterResolvedRecord>(),
            nodeSnapshots: new Dictionary<int, NodeStateSnapshot>()
        );

        // 2. Active Server WebSocket Serialization
        var activeServerMessage = HexWar.Infrastructure.WebSocket.ServerMessage.FromDomainEvent(domainEvent, "room-123", 2, 10);
        var activeBytes = JsonSerializer.SerializeToUtf8Bytes(activeServerMessage, HexWar.Infrastructure.WebSocket.JsonOptions.Default);
        var activeJson = System.Text.Encoding.UTF8.GetString(activeBytes);

        // 3. Redis Serialization (Active Server publishes)
        var pubOptions = DomainJsonOptions.Create();
        var message = new HexWar.Application.Messaging.DistributedEventMessage
        {
            RoomId = "room-123",
            EventType = domainEvent.GetType().Name,
            EventData = JsonSerializer.SerializeToElement(domainEvent, pubOptions),
            SequenceNumber = 10,
            SourceServerId = "server-A",
            Timestamp = DateTime.UtcNow
        };
        var redisJson = JsonSerializer.Serialize(message, pubOptions);

        // 4. Redis Deserialization (Passive Server subscribes)
        var parsedMessage = JsonSerializer.Deserialize<HexWar.Application.Messaging.DistributedEventMessage>(redisJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        });
        
        var eventType = Type.GetType($"HexWar.Domain.Events.{parsedMessage.EventType}, HexWar.Domain");
        var passiveDomainEvent = JsonSerializer.Deserialize(
            parsedMessage.EventData.GetRawText(), eventType, HexWar.Domain.Serialization.DomainEventSerializerOptions.Create()) as IDomainEvent;

        // 5. Passive Server WebSocket Serialization
        var passiveServerMessage = HexWar.Infrastructure.WebSocket.ServerMessage.FromDomainEvent(passiveDomainEvent, "room-123", 2, 10);
        var passiveBytes = JsonSerializer.SerializeToUtf8Bytes(passiveServerMessage, HexWar.Infrastructure.WebSocket.JsonOptions.Default);
        var passiveJson = System.Text.Encoding.UTF8.GetString(passiveBytes);

        using var docActive = JsonDocument.Parse(activeJson);
        using var docPassive = JsonDocument.Parse(passiveJson);

        Assert.That(docPassive.RootElement.GetProperty("type").GetString(), Is.EqualTo(docActive.RootElement.GetProperty("type").GetString()));
        Assert.That(docPassive.RootElement.GetProperty("event_type").GetString(), Is.EqualTo(docActive.RootElement.GetProperty("event_type").GetString()));
        Assert.That(docPassive.RootElement.GetProperty("round").GetInt32(), Is.EqualTo(docActive.RootElement.GetProperty("round").GetInt32()));
        Assert.That(docPassive.RootElement.GetProperty("sequence").GetInt64(), Is.EqualTo(docActive.RootElement.GetProperty("sequence").GetInt64()));
        
        // Compare payloads as raw JSON text
        var activePayload = docActive.RootElement.GetProperty("payload").GetRawText();
        var passivePayload = docPassive.RootElement.GetProperty("payload").GetRawText();
        Assert.That(passivePayload, Is.EqualTo(activePayload));
    }

    private class DummyEventBroadcaster : HexWar.Application.Services.IEventBroadcaster
    {
        public Task BroadcastToRoomAsync(string roomId, IDomainEvent domainEvent, long sequenceNumber = 0) => Task.CompletedTask;
        public Task BroadcastToRoomAsync<T>(string roomId, T message, long sequenceNumber = 0) where T : class => Task.CompletedTask;
        public Task SendToPlayerAsync(string roomId, PlayerSide side, IDomainEvent domainEvent, long sequenceNumber = 0) => Task.CompletedTask;
        public Task SendToPlayerAsync<T>(string roomId, PlayerSide side, T message, long sequenceNumber = 0) where T : class => Task.CompletedTask;
    }

    private class InMemoryGameRoomRepository : HexWar.Application.Services.IGameRoomRepository
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GameRoom> _rooms = new();
        public Task<GameRoom?> GetByIdAsync(string roomId) => Task.FromResult(_rooms.TryGetValue(roomId, out var r) ? r : null);
        public Task SaveAsync(GameRoom gameRoom) { _rooms[gameRoom.RoomId] = gameRoom; return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string roomId) => Task.FromResult(_rooms.ContainsKey(roomId));
        public Task DeleteAsync(string roomId) { _rooms.TryRemove(roomId, out _); return Task.CompletedTask; }
    }

    [Test]
    public void VerifyOwnerAndBackupTimerLogic()
    {
        var repo = new InMemoryGameRoomRepository();
        var broadcaster = new DummyEventBroadcaster();
        var roomId = "timer-test-room";

        // Create GameRoom and set owner to this server
        var room = new GameRoom(roomId);
        room.InitializeMap();
        room.AddPlayer(new PlayerId("player-A"));
        room.AddPlayer(new PlayerId("player-B"));
        room.AssignOwner(HexWar.Application.Services.ServerIdentity.Id);
        repo.SaveAsync(room).Wait();

        // Instantiate GameSession (Owner Server)
        using var ownerSession = new HexWar.Application.Sessions.GameSession(roomId, broadcaster, repo);
        
        // Connect player (triggers timer start)
        ownerSession.OnPlayerConnectedAsync(PlayerSide.A).Wait();

        // Reflection to read private timer fields
        var planningTimerField = typeof(HexWar.Application.Sessions.GameSession).GetField("_planningTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var backupTimerField = typeof(HexWar.Application.Sessions.GameSession).GetField("_backupPlanningTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var ownerPlanningTimer = planningTimerField?.GetValue(ownerSession);
        var ownerBackupTimer = backupTimerField?.GetValue(ownerSession);

        Assert.That(ownerPlanningTimer, Is.Not.Null, "Owner server should start primary timer");
        Assert.That(ownerBackupTimer, Is.Null, "Owner server should NOT start backup timer");

        // Now simulate a game room owned by another server
        var remoteRoomId = "remote-test-room";
        var remoteRoom = new GameRoom(remoteRoomId);
        remoteRoom.InitializeMap();
        remoteRoom.AddPlayer(new PlayerId("player-A"));
        remoteRoom.AddPlayer(new PlayerId("player-B"));
        remoteRoom.AssignOwner("remote-server-999");
        repo.SaveAsync(remoteRoom).Wait();

        // Instantiate GameSession (Passive Server)
        using var passiveSession = new HexWar.Application.Sessions.GameSession(remoteRoomId, broadcaster, repo);
        passiveSession.OnPlayerConnectedAsync(PlayerSide.B).Wait();

        var passivePlanningTimer = planningTimerField?.GetValue(passiveSession);
        var passiveBackupTimer = backupTimerField?.GetValue(passiveSession);

        Assert.That(passivePlanningTimer, Is.Null, "Passive server should NOT start primary timer");
        Assert.That(passiveBackupTimer, Is.Not.Null, "Passive server should start backup timer");
    }
}
