// tests/HexWar.LoadTests/WebSocketLoadTests.cs
namespace HexWar.LoadTests;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HexWar.Infrastructure.WebSocket;

/// <summary>
/// 실제 게임 시나리오 기반 WebSocket 부하 테스트
/// - roomId당 A/B 두 플레이어 연결
/// - gRPC 매치메이킹으로 방 생성
/// - 실제 게임 명령 시뮬레이션
/// - 연결/해제/재연결 시나리오 포함
/// </summary>
public class WebSocketLoadTests
{
    private readonly string _serverUrl;
    private readonly int _gameCount;          // 생성할 게임방 수
    private readonly int _durationSeconds;    // 테스트 지속 시간

    private static readonly (int From, int To, int Count)[] Moves = new[]
    {
        (1, 2, 1),
        (1, 6, 1),
        (5, 4, 1),
        (5, 6, 1),
        (2, 3, 1),
        (4, 3, 1)
    };

    // 측정 지표
    private readonly ConcurrentBag<double> _roundTripTimes = new();
    private readonly ConcurrentBag<double> _moveCommandLatencies = new();
    private int _connectedClients = 0;
    private int _failedConnections = 0;
    private int _messagesSent = 0;
    private int _messagesReceived = 0;
    private int _errors = 0;
    private int _gamesCompleted = 0;
    private int _encountersHandled = 0;
    private readonly ArrayPool<byte> _pool;

    public WebSocketLoadTests(string serverUrl, int gameCount, int durationSeconds, ArrayPool<byte>? pool = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _gameCount = gameCount;
        _durationSeconds = durationSeconds;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"=== WebSocket Game Load Test ===");
        Console.WriteLine($"Server: {_serverUrl}");
        Console.WriteLine($"Games: {_gameCount} ({_gameCount * 2} clients)");
        Console.WriteLine($"Duration: {_durationSeconds}s");
        Console.WriteLine($"================================");

        // 1. gRPC로 방 생성 및 매칭
        var roomIds = await CreateGameRoomsAsync(_gameCount);
        Console.WriteLine($"Created {roomIds.Count} game rooms");

        // 2. WebSocket 연결 및 게임 시뮬레이션
        using var cts = new CancellationTokenSource();
        var tasks = new List<Task>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var roomId in roomIds)
        {
            // Player A 연결
            tasks.Add(Task.Run(() => SimulatePlayerAsync(roomId, "A", cts.Token)));

            // 약간의 간격으로 Player B 연결 (동시 폭주 방지)
            await Task.Delay(Random.Shared.Next(1, 6));
            tasks.Add(Task.Run(() => SimulatePlayerAsync(roomId, "B", cts.Token)));
        }

        // 3. 진행 상황 모니터링
        while (!cts.Token.IsCancellationRequested && stopwatch.Elapsed.TotalSeconds < _durationSeconds)
        {
            await Task.Delay(1000);

            var memory = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            var process = Process.GetCurrentProcess();
            var ws = process.WorkingSet64 / 1024.0 / 1024.0;

            Console.WriteLine(
                $"[{stopwatch.Elapsed:hh\\:mm\\:ss}] " +
                $"Clients: {_connectedClients}/{_gameCount * 2} | " +
                $"Msg: {_messagesSent}s/{_messagesReceived}r | " +
                $"Err: {_errors} | " +
                $"Games done: {_gamesCompleted} | " +
                $"Encounters: {_encountersHandled} | " +
                $"Mem: {memory:F0}MB GC / {ws:F0}MB WS");
        }

        // 4. 종료 및 정리
        Console.WriteLine("\nStopping test...");
        cts.Cancel();

        try { await Task.WhenAll(tasks); } catch { }

        stopwatch.Stop();
        PrintReport(stopwatch.Elapsed);
    }

    /// <summary>
    /// gRPC 매치메이킹으로 게임방 생성
    /// gRPC 사용이 어려운 경우 REST fallback
    /// </summary>
    private async Task<List<string>> CreateGameRoomsAsync(int count)
    {
        var roomIds = new List<string>();
        var httpClient = new HttpClient { BaseAddress = new Uri(_serverUrl) };

        // 두 명씩 짝지어 방 생성
        for (int i = 0; i < count; i++)
        {
            try
            {
                // Player A 매칭 요청
                var responseA = await httpClient.PostAsync(
                    "/api/matchmaking/join",
                    new StringContent(
                        JsonSerializer.Serialize(new { playerId = $"load-A-{i}" }),
                        Encoding.UTF8, "application/json"));

                // Player B 매칭 요청 (거의 동시에)
                var responseB = await httpClient.PostAsync(
                    "/api/matchmaking/join",
                    new StringContent(
                        JsonSerializer.Serialize(new { playerId = $"load-B-{i}" }),
                        Encoding.UTF8, "application/json"));

                if (responseA.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<MatchResponse>(
                        await responseA.Content.ReadAsStringAsync(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (result?.RoomId != null && result.RoomId != string.Empty)
                    {
                        roomIds.Add(result.RoomId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create room {i}: {ex.Message}");
            }

            // 서버 부하 분산을 위한 간격
            if (i % 10 == 9) await Task.Delay(500);
        }

        return roomIds;
    }

    /// <summary>
    /// 한 명의 플레이어를 시뮬레이션합니다.
    /// 실제 게임처럼 행동합니다.
    /// </summary>
    private async Task SimulatePlayerAsync(string roomId, string side, CancellationToken cancellationToken)
    {
        using var client = new ClientWebSocket();
        var wsUrl = _serverUrl.Replace("https://", "wss://").Replace("http://", "ws://");
        var uri = new Uri($"{wsUrl}/ws/game/{roomId}/{side}");

        // 연결
        try
        {
            await client.ConnectAsync(uri, cancellationToken);
            Interlocked.Increment(ref _connectedClients);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedConnections);
            Interlocked.Increment(ref _errors);
            Console.WriteLine($"Connection failed for room {roomId} (Side: {side}): {ex.Message} ({ex.GetType().Name})");
            return;
        }

        // 연결 성공 후 버퍼 대여 및 게임 수행
        var buffer = _pool.Rent(1024 * 8);
        try
        {
            // 연결 성공 후 초기 상태 수신
            await ReceiveInitialStateAsync(client, buffer, cancellationToken);

            // 게임 시뮬레이션 루프
            await GameplayLoopAsync(client, buffer, roomId, side, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
        }
        finally
        {
            _pool.Return(buffer);

            // 정리
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Test end", CancellationToken.None);
                }
                catch { }
            }
            Interlocked.Decrement(ref _connectedClients);
        }
    }

    /// <summary>
    /// 게임 플레이 루프: 실제 게임처럼 행동
    /// </summary>
    private async Task GameplayLoopAsync(
        ClientWebSocket client, byte[] buffer, string roomId, string side, CancellationToken cancellationToken)
    {
        var random = new Random(Guid.NewGuid().GetHashCode());
        var moveInterval = TimeSpan.FromSeconds(random.Next(2, 5)); // 2~5초마다 이동
        var nextMoveTime = DateTime.UtcNow.Add(moveInterval);

        while (client.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            // 1. 서버 메시지 수신 (이벤트, 상태 업데이트 등)
            var serverMessage = await ReceiveMessageAsync(client, buffer, cancellationToken);

            if (serverMessage != null)
            {
                Interlocked.Increment(ref _messagesReceived);
                await HandleServerMessageAsync(serverMessage, client, roomId, side, cancellationToken);
            }

            // 2. 이동 명령 (일정 간격으로)
            if (DateTime.UtcNow >= nextMoveTime)
            {
                await SendMoveCommandAsync(client, side, cancellationToken);
                Interlocked.Increment(ref _messagesSent);
                nextMoveTime = DateTime.UtcNow.Add(moveInterval);
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// 서버 메시지 처리
    /// </summary>
    private async Task HandleServerMessageAsync(
        ServerMessage message, ClientWebSocket client,
        string roomId, string side, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "game_event" when message.EventType == "EncounterOccurred":
                Interlocked.Increment(ref _encountersHandled);

                // 조우 발생 → 랜덤하게 전진/복귀 결정
                var decision = Random.Shared.Next(2) == 0 ? "Advance" : "Retreat";
                await SendEncounterDecisionAsync(client, message, decision, ct);
                Interlocked.Increment(ref _messagesSent);
                break;

            case "game_event" when message.EventType == "GameOver":
                Interlocked.Increment(ref _gamesCompleted);
                break;
        }
    }

    /// <summary>
    /// 유닛 이동 명령 전송
    /// </summary>
    private async Task SendMoveCommandAsync(
        ClientWebSocket client, string side, CancellationToken ct)
    {
        var move = Moves[Random.Shared.Next(Moves.Length)];

        var moveMessage = new
        {
            type = "move_units",
            payload = new { from = move.From, to = move.To, count = move.Count }
        };

        var sw = Stopwatch.StartNew();
        await SendJsonAsync(client, moveMessage, ct);
        sw.Stop();

        _moveCommandLatencies.Add(sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 조우 결정 전송
    /// </summary>
    private async Task SendEncounterDecisionAsync(
        ClientWebSocket client, ServerMessage encounterEvent, string decision, CancellationToken ct)
    {
        // EncounterOccurred 이벤트에서 간선 정보 추출
        if (encounterEvent.Payload is JsonElement payload)
        {
            var fromNodeEl = payload.GetProperty("fromNode");
            var fromNode = fromNodeEl.ValueKind == JsonValueKind.Number 
                ? fromNodeEl.GetInt32() 
                : fromNodeEl.GetProperty("value").GetInt32();

            var toNodeEl = payload.GetProperty("toNode");
            var toNode = toNodeEl.ValueKind == JsonValueKind.Number 
                ? toNodeEl.GetInt32() 
                : toNodeEl.GetProperty("value").GetInt32();

            var decisionMessage = new
            {
                type = "encounter_decision",
                payload = new { from_node = fromNode, to_node = toNode, decision }
            };

            await SendJsonAsync(client, decisionMessage, ct);
        }
    }

    /// <summary>
    /// 초기 상태 수신 대기
    /// </summary>
    private async Task ReceiveInitialStateAsync(ClientWebSocket client, byte[] buffer, CancellationToken ct)
    {
        // get_state 요청
        var getStateMessage = new { type = "get_state" };
        await SendJsonAsync(client, getStateMessage, ct);

        // GameStarted 또는 GameState 이벤트 수신 대기
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            var message = await ReceiveMessageAsync(client, buffer, ct);
            if (message?.EventType == "GameStarted" || message?.EventType == "GameState") break;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// WebSocket 메시지 수신
    /// </summary>
    private async Task<ServerMessage?> ReceiveMessageAsync(
        ClientWebSocket client, byte[] buffer, CancellationToken ct)
    {
        try
        {
            var firstResult = await client.ReceiveAsync(buffer.AsMemory(), ct);

            if (firstResult.MessageType == WebSocketMessageType.Close)
                return null;

            if (firstResult.EndOfMessage)
            {
                // Fast path: fits in one receive buffer chunk. No MemoryStream allocated.
                return JsonSerializer.Deserialize<ServerMessage>(buffer.AsSpan(0, firstResult.Count), JsonOptions.Default);
            }
            else
            {
                // Slow path: multi-packet message. Fallback to MemoryStream.
                using var ms = new MemoryStream();
                ms.Write(buffer, 0, firstResult.Count);

                ValueWebSocketReceiveResult loopResult;
                do
                {
                    loopResult = await client.ReceiveAsync(buffer.AsMemory(), ct);
                    ms.Write(buffer, 0, loopResult.Count);
                }
                while (!loopResult.EndOfMessage);

                if (loopResult.MessageType == WebSocketMessageType.Close)
                    return null;

                ms.Position = 0;
                return await JsonSerializer.DeserializeAsync<ServerMessage>(ms, JsonOptions.Default, ct);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
    }

    /// <summary>
    /// JSON 메시지 전송
    /// </summary>
    private async Task SendJsonAsync(
        ClientWebSocket client, object message, CancellationToken ct)
    {
        if (client.State != WebSocketState.Open) return;

        try
        {
            var buffer = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions.Default);
            await client.SendAsync(
                buffer,
                WebSocketMessageType.Text, true, ct);
        }
        catch { }
    }

    /// <summary>
    /// 최종 리포트
    /// </summary>
    private void PrintReport(TimeSpan elapsed)
    {
        var rttList = _roundTripTimes.ToArray();
        var moveLatencyList = _moveCommandLatencies.ToArray();

        Console.WriteLine("\n===========================================");
        Console.WriteLine("          GAME LOAD TEST REPORT            ");
        Console.WriteLine("===========================================");
        Console.WriteLine($"Duration: {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Target Games: {_gameCount} ({_gameCount * 2} clients)");
        Console.WriteLine($"Peak Connections: {_connectedClients}");
        Console.WriteLine($"Failed Connections: {_failedConnections}");
        Console.WriteLine($"Messages Sent: {_messagesSent}");
        Console.WriteLine($"Messages Received: {_messagesReceived}");
        Console.WriteLine($"Games Completed: {_gamesCompleted}");
        Console.WriteLine($"Encounters Handled: {_encountersHandled}");
        Console.WriteLine($"Errors: {_errors}");
        Console.WriteLine("-------------------------------------------");
        Console.WriteLine("           COMMAND LATENCY (ms)            ");
        Console.WriteLine("-------------------------------------------");
        Console.WriteLine($"Move Avg: {(moveLatencyList.Any() ? moveLatencyList.Average() : 0):F2}");
        Console.WriteLine($"Move Min: {(moveLatencyList.Any() ? moveLatencyList.Min() : 0):F2}");
        Console.WriteLine($"Move Max: {(moveLatencyList.Any() ? moveLatencyList.Max() : 0):F2}");
        Console.WriteLine($"Move P95: {Percentile(moveLatencyList, 95):F2}");
        Console.WriteLine($"Move P99: {Percentile(moveLatencyList, 99):F2}");
        Console.WriteLine($"Samples: {moveLatencyList.Length}");
        Console.WriteLine("-------------------------------------------");
        Console.WriteLine("           SERVER RESOURCE                ");
        Console.WriteLine("-------------------------------------------");

        var process = Process.GetCurrentProcess();
        Console.WriteLine($"Working Set: {process.WorkingSet64 / 1024.0 / 1024.0:F1}MB");
        Console.WriteLine($"GC Heap: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1}MB");
        Console.WriteLine($"Gen0 GC: {GC.CollectionCount(0)}");
        Console.WriteLine($"Gen1 GC: {GC.CollectionCount(1)}");
        Console.WriteLine($"Gen2 GC: {GC.CollectionCount(2)}");
        Console.WriteLine("===========================================");
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        if (!sorted.Any()) return 0;
        Array.Sort(sorted);
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Length - 1))];
    }
}

// 매치메이킹 응답 DTO
public class MatchResponse
{
    public string RoomId { get; set; } = string.Empty;
    public string PlayerSide { get; set; } = string.Empty;
    public string OpponentId { get; set; } = string.Empty;
}