namespace HexWar.Server.WebSocket;

using HexWar.Infrastructure.WebSocket;

/// <summary>
/// ASP.NET Core WebSocket 연결을 처리하는 미들웨어
/// </summary>
public class WebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GameWebSocketHandler _handler;

    public WebSocketMiddleware(RequestDelegate next, GameWebSocketHandler handler)
    {
        _next = next;
        _handler = handler;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // WebSocket 경로: /ws/game/{roomId}/{playerSide}
        if (context.Request.Path.StartsWithSegments("/ws/game"))
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var pathParts = context.Request.Path.Value?.Split('/');
                if (pathParts?.Length >= 4)
                {
                    var roomId = pathParts[3];
                    var playerSide = pathParts.Length >= 5 ? pathParts[4] : null;

                    if (!string.IsNullOrEmpty(roomId) && !string.IsNullOrEmpty(playerSide))
                    {
                        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                        await _handler.HandleConnectionAsync(webSocket, roomId, playerSide);
                        return;
                    }
                }
            }

            context.Response.StatusCode = 400;
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// 미들웨어 등록 확장 메서드
/// </summary>
public static class WebSocketMiddlewareExtensions
{
    public static IApplicationBuilder UseGameWebSocket(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<WebSocketMiddleware>();
    }
}