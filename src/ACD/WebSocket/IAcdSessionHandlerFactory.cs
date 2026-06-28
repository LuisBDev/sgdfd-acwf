using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACWF.WebSocket;

public interface IAcwfSessionHandlerFactory
{
    AcwfSessionHandler Create(string sessionId, NativeWebSocket webSocket, IServiceScope scope);
}