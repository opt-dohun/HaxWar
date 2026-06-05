using HexWar.Application.Services;
using HexWar.Application.Sessions;
using HexWar.Infrastructure.Persistence;
using HexWar.Infrastructure.WebSocket;
using HexWar.Server.WebSocket;

var builder = WebApplication.CreateBuilder(args);

// ========================================================================
// 인프라 서비스 등록 (Singleton - 전체 앱에서 공유)
// ========================================================================

// WebSocket 연결 관리자
builder.Services.AddSingleton<ConnectionManager>();

// 이벤트 브로드캐스터
builder.Services.AddSingleton<IEventBroadcaster>(sp =>
{
    var connectionManager = sp.GetRequiredService<ConnectionManager>();
    var sessionRegistry = sp.GetRequiredService<SessionRegistry>();

    return new InMemoryEventBroadcaster(
        connectionManager,
        roomId =>
        {
            var session = sessionRegistry.GetSession(roomId);
            return session?.CurrentRound ?? 0;
        });
});

// GameRoom 저장소 (인메모리)
builder.Services.AddSingleton<IGameRoomRepository, InMemoryGameRoomRepository>();

// 세션 레지스트리
builder.Services.AddSingleton<SessionRegistry>();

// WebSocket 핸들러
builder.Services.AddSingleton<GameWebSocketHandler>();

var app = builder.Build();

// WebSocket 지원 활성화
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// WebSocket 미들웨어 등록
app.UseGameWebSocket();

app.Run();