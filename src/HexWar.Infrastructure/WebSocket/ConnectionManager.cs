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
        var socket = GetConnection(roomId, playerSide);
        if (socket == null || socket.State != WebSocketState.Open) return;

        var buffer = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    // 해당 방의 모든 사용자에게 메시지 전송 
    public async Task BroadcastToRoomAsync(string roomId, string message)
    {
        var connections = GetRoomConnections(roomId);

        var tasks = connections.Select(async socket =>
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    var buffer = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer),
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
    public async Task CleanupRoomAsync(string roomId)
    {
        var connections = GetRoomConnections(roomId);
        foreach (var socket in connections)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Room closed", CancellationToken.None);
                }
            }
            catch { }
            // 메모리에 등록된 연결 정보 삭제
            RemoveConnection(socket);
        }
    }


}