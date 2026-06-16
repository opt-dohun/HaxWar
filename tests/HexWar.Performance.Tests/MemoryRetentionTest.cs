#pragma warning disable CS8618

namespace HexWar.Performance.Tests;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HexWar.Application.Sessions;
using HexWar.Application.Services;
using HexWar.Domain.Commands;
using HexWar.Domain.Entities;
using HexWar.Domain.Enums;
using HexWar.Domain.Exceptions;
using HexWar.Domain.Events;
using HexWar.Domain.ValueObjects;
using HexWar.Infrastructure.Persistence;
using HexWar.Infrastructure.WebSocket;
using NUnit.Framework;

public class DummyBroadcaster : IEventBroadcaster
{
    public Task BroadcastToRoomAsync(string roomId, IDomainEvent domainEvent, long sequenceNumber = 0) => Task.CompletedTask;
    public Task BroadcastToRoomAsync<T>(string roomId, T message, long sequenceNumber = 0) where T : class => Task.CompletedTask;
    public Task SendToPlayerAsync(string roomId, PlayerSide side, IDomainEvent domainEvent, long sequenceNumber = 0) => Task.CompletedTask;
    public Task SendToPlayerAsync<T>(string roomId, PlayerSide side, T message, long sequenceNumber = 0) where T : class => Task.CompletedTask;
}

[TestFixture]
public class MemoryRetentionTest
{
    private ConnectionManager _connectionManager;
    private IGameRoomRepository _repository;
    private SessionRegistry _registry;

    [SetUp]
    public void Setup()
    {
        _connectionManager = new ConnectionManager();
        _repository = new InMemoryGameRoomRepository();
        _registry = new SessionRegistry(_repository, new DummyBroadcaster());
    }

    /// <summary>
    /// 100게임을 생성/진행/종료한 후 메모리 누수가 없는지 확인합니다.
    /// </summary>
    [Test]
    public async Task CompleteGame_ShouldNotLeakMemory()
    {
        // Arrange
        var initialMemory = GC.GetTotalMemory(true);

        // Act: 100게임 시뮬레이션
        for (int i = 0; i < 100; i++)
        {
            var roomId = $"room-{i}";
            var session = await _registry.CreateSessionAsync(roomId);
            var gameRoom = GetGameRoom(session);

            // 플레이어 추가
            gameRoom.AddPlayer(new PlayerId($"player-{i}-A"));
            gameRoom.AddPlayer(new PlayerId($"player-{i}-B"));

            // 실제 게임처럼 여러 라운드 진행
            SimulateGame(gameRoom, maxRounds: 10);

            // 세션 제거
            _registry.RemoveSession(roomId);
        }

        // 강제 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var finalMemory = GC.GetTotalMemory(true);
        var leaked = finalMemory - initialMemory;

        TestContext.Progress.WriteLine($"Memory before: {initialMemory / 1024.0:F1}KB");
        TestContext.Progress.WriteLine($"Memory after:  {finalMemory / 1024.0:F1}KB");
        TestContext.Progress.WriteLine($"Leaked:        {leaked / 1024.0:F1}KB");

        Assert.That(leaked, Is.LessThan(5 * 1024 * 1024),
            $"Memory leak detected: {leaked / 1024.0:F1}KB retained after 100 games");
    }

    /// <summary>
    /// 실제 게임 흐름을 시뮬레이션합니다.
    /// 유닛이 고갈되지 않도록 이동 패턴을 조정합니다.
    /// </summary>
    private void SimulateGame(GameRoom gameRoom, int maxRounds)
    {
        var random = new Random(42); // 결정적 시드

        for (int round = 0; round < maxRounds; round++)
        {
            if (gameRoom.Phase == GamePhase.GameOver) break;

            // Player A의 이동
            SimulatePlayerMoves(gameRoom, PlayerSide.A, random);

            // Player B의 이동
            SimulatePlayerMoves(gameRoom, PlayerSide.B, random);

            // 라운드 해소
            if (gameRoom.Phase == GamePhase.Planning)
            {
                gameRoom.ResolveRound();
            }
        }
    }

    /// <summary>
    /// 한 플레이어의 이동을 시뮬레이션합니다.
    /// 유닛이 있는 노드에서만 이동을 시도합니다.
    /// </summary>
    private void SimulatePlayerMoves(GameRoom gameRoom, PlayerSide side, Random random)
    {
        int unitsToMove = random.Next(0, 4); // 0~3기 이동

        for (int i = 0; i < unitsToMove; i++)
        {
            // 이동 가능한 유닛이 있는 노드 찾기
            var availableNodes = gameRoom.Nodes.Values
                .Where(n => !n.IsHeadquarters) // 본부에서는 출발 불가 (본부 점령 불가)
                .Where(n => n.GetMobileCount(side) > 0)
                .ToList();

            if (!availableNodes.Any()) break;

            // 랜덤하게 출발 노드 선택
            var fromNode = availableNodes[random.Next(availableNodes.Count)];

            // 이웃 노드 중에서 랜덤하게 목적지 선택
            var neighbors = fromNode.Neighbors
                .Where(n => !gameRoom.Nodes[n].IsHeadquarters) // 본부로 이동 가능
                .ToList();

            if (!neighbors.Any()) continue;

            var toNode = neighbors[random.Next(neighbors.Count)];

            try
            {
                gameRoom.MoveUnits(side, new MoveCommand(
                    fromNode.Id, toNode, 1));
            }
            catch (DomainException)
            {
                // 이동 불가능하면 건너뜀
            }
        }
    }

    /// <summary>
    /// 장시간 게임에서 Gen2 컬렉션이 과도하게 발생하지 않는지 확인합니다.
    /// </summary>
    [Test]
    public void LongRunningGame_ShouldNotCauseExcessiveGen2Collections()
    {
        // Arrange
        var gameRoom = new GameRoom("test-room");
        gameRoom.InitializeMap();
        gameRoom.AddPlayer(new PlayerId("p1"));
        gameRoom.AddPlayer(new PlayerId("p2"));

        var gen2Before = GC.CollectionCount(2);
        var random = new Random(42);

        // Act: 50라운드 진행 (충분한 게임 길이)
        for (int round = 0; round < 50; round++)
        {
            if (gameRoom.Phase == GamePhase.GameOver) break;

            // Player A: 유닛이 있는 노드에서 이동
            MoveFromAvailableNodes(gameRoom, PlayerSide.A, random);

            // Player B: 유닛이 있는 노드에서 이동
            MoveFromAvailableNodes(gameRoom, PlayerSide.B, random);

            // 라운드 해소
            if (gameRoom.Phase == GamePhase.Planning)
            {
                gameRoom.ResolveRound();

                // 조우 해소 (자동으로 Retreat)
                ResolveAllPendingEncounters(gameRoom);
            }
        }

        var gen2After = GC.CollectionCount(2);
        var gen2Collections = gen2After - gen2Before;

        TestContext.Progress.WriteLine($"Rounds completed: {gameRoom.CurrentRound - 1}");
        TestContext.Progress.WriteLine($"Gen2 collections during test: {gen2Collections}");
        TestContext.Progress.WriteLine($"Final phase: {gameRoom.Phase}");

        // Gen2 컬렉션이 3회 미만이어야 함
        Assert.That(gen2Collections, Is.LessThan(3),
            $"Too many Gen2 collections ({gen2Collections}). Memory pressure detected.");
    }

    /// <summary>
    /// 유닛이 있는 노드에서만 이동을 시도합니다.
    /// </summary>
    private void MoveFromAvailableNodes(GameRoom gameRoom, PlayerSide side, Random random)
    {
        // 이동 가능한 노드 찾기
        var nodesWithUnits = gameRoom.Nodes.Values
            .Where(n => n.GetMobileCount(side) > 0)
            .ToList();

        foreach (var node in nodesWithUnits)
        {
            int unitsAtNode = node.GetMobileCount(side);
            int unitsToMove = random.Next(0, unitsAtNode + 1);

            if (unitsToMove <= 0) continue;

            var neighbors = node.Neighbors.ToList();
            if (!neighbors.Any()) continue;

            var targetNode = neighbors[random.Next(neighbors.Count)];

            try
            {
                gameRoom.MoveUnits(side, new MoveCommand(
                    node.Id, targetNode, unitsToMove));
            }
            catch (DomainException)
            {
                // 무시
            }
        }
    }

    /// <summary>
    /// 대기 중인 모든 조우를 자동으로 해소합니다 (Retreat).
    /// </summary>
    private void ResolveAllPendingEncounters(GameRoom gameRoom)
    {
        var pendingEncounters = gameRoom.PendingEncounters.ToList();

        foreach (var encounter in pendingEncounters)
        {
            try
            {
                if (!encounter.HasDecided(PlayerSide.A))
                    gameRoom.ResolveEncounter(encounter.EdgeId, PlayerSide.A, EncounterDecision.Retreat);

                if (!encounter.HasDecided(PlayerSide.B))
                    gameRoom.ResolveEncounter(encounter.EdgeId, PlayerSide.B, EncounterDecision.Retreat);
            }
            catch (DomainException)
            {
                // 이미 해소된 경우 무시
            }
        }
    }

    /// <summary>
    /// 이벤트 생성 및 브로드캐스트로 인한 Gen0 GC 압박을 확인합니다.
    /// </summary>
    [Test]
    public void DomainEvents_ShouldNotCauseExcessiveGen0Collections()
    {
        // Arrange
        var gameRoom = new GameRoom("event-test");
        gameRoom.InitializeMap();
        gameRoom.AddPlayer(new PlayerId("p1"));
        gameRoom.AddPlayer(new PlayerId("p2"));

        var gen0Before = GC.CollectionCount(0);

        // Act: 20라운드 진행 (일반적인 게임 길이)
        var random = new Random(42);
        for (int round = 0; round < 20; round++)
        {
            if (gameRoom.Phase == GamePhase.GameOver) break;

            MoveFromAvailableNodes(gameRoom, PlayerSide.A, random);
            MoveFromAvailableNodes(gameRoom, PlayerSide.B, random);

            if (gameRoom.Phase == GamePhase.Planning)
            {
                gameRoom.ResolveRound();
                ResolveAllPendingEncounters(gameRoom);
            }
        }

        var gen0After = GC.CollectionCount(0);
        var gen0Collections = gen0After - gen0Before;

        TestContext.Progress.WriteLine($"Gen0 collections during 20-round game: {gen0Collections}");

        // Gen0 컬렉션은 많이 발생해도 정상 (수명이 짧은 객체들)
        // 하지만 과도하지 않아야 함 (50회 미만 정도 예상)
        Assert.That(gen0Collections, Is.LessThan(100),
            $"Excessive Gen0 collections ({gen0Collections}). Consider event pooling.");
    }

    /// <summary>
    /// 리플렉션으로 GameSession에서 GameRoom을 가져옵니다.
    /// 실제 코드에서는 internal 접근자나 InternalsVisibleTo를 사용하는 것이 좋습니다.
    /// </summary>
    private GameRoom GetGameRoom(GameSession session)
    {
        return _repository.GetByIdAsync(session.RoomId).GetAwaiter().GetResult()!;
    }
}