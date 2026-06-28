using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ACWF.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACWF.WebSocket;

/// <summary>
///     Funciones auxiliares para E/S de frames WebSocket. Sin estado interno.
/// </summary>
public static class WebSocketTransport
{
    private const int BufferSize = 64 * 1024;

    public static async Task<(FrameKind Kind, byte[]? Payload)> ReceiveFrameAsync(
        NativeWebSocket webSocket,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[BufferSize];
        WebSocketReceiveResult result;

        do
        {
            result = await webSocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return (FrameKind.Close, null);

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var data = ms.ToArray();
        var kind = result.MessageType == WebSocketMessageType.Binary ? FrameKind.Binary : FrameKind.Text;
        return (kind, data);
    }

    public static async Task SendJsonAsync<T>(
        NativeWebSocket webSocket,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        await webSocket.SendAsync(json, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public static async Task SendErrorAndCloseAsync(
        NativeWebSocket webSocket,
        string code,
        string message,
        int closeCode,
        ILogger logger,
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            await SendJsonAsync(webSocket,
                new ErrorMessage(code, message),
                AcwfJsonContext.Default.ErrorMessage, ct);

            var status = closeCode switch
            {
                1008 => WebSocketCloseStatus.PolicyViolation,
                1011 => WebSocketCloseStatus.InternalServerError,
                1000 => WebSocketCloseStatus.NormalClosure,
                1001 => WebSocketCloseStatus.EndpointUnavailable,
                _ => WebSocketCloseStatus.InternalServerError
            };

            await CloseWebSocketAsync(webSocket, status, code, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{SessionId}] Error al enviar el frame de error", sessionId);
        }
    }

    public static async Task CloseWebSocketAsync(
        NativeWebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken ct)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await webSocket.CloseAsync(status, description, ct).ConfigureAwait(false);
    }
}

public enum FrameKind
{
    Text,
    Binary,
    Close
}