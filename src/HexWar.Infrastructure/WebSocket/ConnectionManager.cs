namespace HexWar.Infrastructure.WebSocket;

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class ConnectionManager
{
    // Key: (roomId, playerSide), Value: WebSocket 연결
    private readonly ConcurrentDictionary<(string RoomId, string PlayerSide), System.Net.WebSockets.WebSocket> _connections = new();

    // 역방향 조회용: WebSocket → (roomId, playerSide)
    private readonly ConcurrentDictionary<System.Net.WebSockets.WebSocket, (string RoomId, string PlayerSide)> _reverseLookup = new();

    public void AddConnection(string roomId, string playerSide, System.Net.WebSockets.WebSocket socket)
    {
        var key = (roomId, playerSide);
        _connections[key] = socket;
        _reverseLookup[socket] = key;
    }

    public void RemoveConnection(System.Net.WebSockets.WebSocket socket)
    {
        if (_reverseLookup.TryRemove(socket, out var key))
        {
            _connections.TryRemove(key, out _);
        }
    }

    public int GetTotalConnectionCount()
    {
        return _connections.Count(kvp => kvp.Value.State == WebSocketState.Open);
    }

    public async Task CleanupRoomAsync(string roomId)
    {
        var roomKeys = _connections.Keys
            .Where(k => k.RoomId == roomId)
            .ToList();

        var closeTasks = new List<Task>();

        foreach (var key in roomKeys)
        {
            if (_connections.TryGetValue(key, out var socket))
            {
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        // 정상 종료 시도
                        closeTasks.Add(socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Room closed by server",
                            CancellationToken.None));
                    }
                    else if (socket.State == WebSocketState.Aborted)
                    {
                        // 이미 비정상 종료됨 → 바로 제거
                        _connections.TryRemove(key, out _);
                    }
                }
                catch
                {
                    // 소켓이 이미 망가진 경우 강제 제거
                    _connections.TryRemove(key, out _);
                }
            }
        }

        // 모든 CloseAsync가 완료될 때까지 대기 (최대 3초)
        if (closeTasks.Any())
        {
            var timeout = Task.Delay(3000);
            var completed = await Task.WhenAny(Task.WhenAll(closeTasks), timeout);

            if (completed == timeout)
            {
                Console.WriteLine("Timeout closing some connections for room " + roomId);
            }
        }

        // 남은 연결 강제 제거
        foreach (var key in roomKeys)
        {
            _connections.TryRemove(key, out _);
        }
    }

    public int CleanupStaleConnections()
    {
        var staleKeys = _connections
            .Where(kvp => kvp.Value.State != WebSocketState.Open && kvp.Value.State != WebSocketState.Connecting)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _connections.TryRemove(key, out _);
        }

        return staleKeys.Count;
    }

    // 소켓을 기준으로 역뱡향 조회를 진행하여 사용자 정보 획득
    // SessionRegistry에 전달하여 PlayerSessionState 매핑
    public (string RoomId, string PlayerSide)? GetConnectionInfo(System.Net.WebSockets.WebSocket socket)
    {
        return _reverseLookup.TryGetValue(socket, out var info) ? info : null;
    }

    // 사용자 정보를 기준으로 WebSocket 연결 획득 - 메시지 전송 시 활용
    public System.Net.WebSockets.WebSocket? GetConnection(string roomId, string playerSide)
    {
        return _connections.TryGetValue((roomId, playerSide), out var socket) ? socket : null;
    }

    // 특정 방의 모든 사용자 WebSocket 연결 획득 - 브로드캐스팅용
    public List<System.Net.WebSockets.WebSocket> GetRoomConnections(string roomId)
    {
        return _connections
            .Where(kvp => kvp.Key.RoomId == roomId)
            .Select(kvp => kvp.Value)
            .Where(s => s.State == WebSocketState.Open)
            .ToList();
    }

    // 특정 플레이어에게 메시지 전송     
    public async Task SendToPlayerAsync(string roomId, string playerSide, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await SendToPlayerAsync(roomId, playerSide, bytes);
    }

    public async Task SendToPlayerAsync(string roomId, string playerSide, ReadOnlyMemory<byte> messageBytes)
    {
        var socket = GetConnection(roomId, playerSide);
        if (socket == null || socket.State != WebSocketState.Open) return;

        await socket.SendAsync(
            messageBytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    // 해당 방의 모든 사용자에게 메시지 전송 
    public async Task BroadcastToRoomAsync(string roomId, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await BroadcastToRoomAsync(roomId, bytes);
    }

    public async Task BroadcastToRoomAsync(string roomId, ReadOnlyMemory<byte> messageBytes)
    {
        var connections = GetRoomConnections(roomId);

        var tasks = connections.Select(async socket =>
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        messageBytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        CancellationToken.None);
                }
            }
            catch
            {
                // 연결이 끊긴 소켓은 무시
            }
        });

        await Task.WhenAll(tasks);
    }

    public int GetRoomPlayerCount(string roomId)
    {
        return _connections.Count(kvp => kvp.Key.RoomId == roomId && kvp.Value.State == WebSocketState.Open);
    }

    // 방의 모든 연결을 정리합니다.
    // public async Task CleanupRoomAsync(string roomId)
    // {
    //     var connections = GetRoomConnections(roomId);
    //     foreach (var socket in connections)
    //     {
    //         try
    //         {
    //             if (socket.State == WebSocketState.Open)
    //             {
    //                 await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Room closed", CancellationToken.None);
    //             }
    //         }
    //         catch { }
    //         // 메모리에 등록된 연결 정보 삭제
    //         RemoveConnection(socket);
    //     }
    // }


}