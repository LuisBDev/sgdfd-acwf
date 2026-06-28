using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using ACWF.Firma;
using ACWF.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACWF.WebSocket;

/// <summary>
///     Maneja el ciclo de vida completo de una sesión WebSocket vía una state machine.
///     Se instancia por sesión desde AcwfWebSocketMiddleware a través de IAcwfSessionHandlerFactory.
/// </summary>
public sealed class AcwfSessionHandler
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly FirmaWorkflowHandler _firmaHandler;
    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly string _watchDirectory;
    private string? _authToken;

    private SessionState _state = SessionState.Connected;

    public AcwfSessionHandler(
        FirmaWorkflowHandler firmaHandler,
        ILogger logger,
        string sessionId,
        string watchDirectory)
    {
        _firmaHandler = firmaHandler;
        _logger = logger;
        _sessionId = sessionId;
        _watchDirectory = watchDirectory;
    }

    public async Task HandleAsync(NativeWebSocket webSocket, CancellationToken ct)
    {
        _logger.LogInformation("[{SessionId}] Sesión iniciada", _sessionId);

        try
        {
            // Enviar CONNECTED al cliente inmediatamente después del upgrade.
            var connected = new ConnectedMessage(
                AgentVersion,
                "READY",
                _watchDirectory);
            await WebSocketTransport.SendJsonAsync(webSocket, connected, AcwfJsonContext.Default.ConnectedMessage, ct);

            // Loop principal de la state machine.
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (kind, payload) = await WebSocketTransport.ReceiveFrameAsync(webSocket, ct);

                if (kind == FrameKind.Close)
                {
                    _logger.LogInformation("[{SessionId}] El cliente cerró la conexión", _sessionId);
                    break;
                }

                if (kind == FrameKind.Binary)
                {
                    if (_state != SessionState.ReceivingFile || _firmaHandler.CurrentFilename is null)
                    {
                        _logger.LogWarning("[{SessionId}] Se recibió un frame binario en estado inesperado {State}", _sessionId, _state);
                        await WebSocketTransport.SendErrorAndCloseAsync(webSocket, "UNEXPECTED_MESSAGE", "Binary frame received in wrong state", 1011, _logger, _sessionId, ct);
                        return;
                    }

                    _state = await _firmaHandler.HandleBinaryFrameAsync(webSocket, payload!, ct);
                    continue;
                }

                // Text frame — deserializar y despachar.
                if (payload is null) continue;

                var baseMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.BaseMessage);
                if (baseMsg is null)
                {
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, "UNKNOWN_MESSAGE_TYPE", "Could not parse message type", 1011, _logger, _sessionId, ct);
                    return;
                }

                await DispatchTextMessageAsync(webSocket, baseMsg.Type, payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{SessionId}] Sesión cancelada", _sessionId);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("[{SessionId}] Conexión WebSocket cerrada prematuramente", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Excepción no controlada en el manejador de sesión", _sessionId);
            try
            {
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, "INTERNAL_ERROR", ex.Message, 1011, _logger, _sessionId, ct);
            }
            catch
            {
                /* Limpieza al finalizar */
            }
        }
        finally
        {
            if (_state != SessionState.Idle && _firmaHandler.CurrentFilename is not null)
            {
                _logger.LogInformation("[{SessionId}] Sesión interrumpida en estado {State}, limpiando archivos", _sessionId, _state);
                _firmaHandler.Cleanup();
            }

            _state = SessionState.Closed;
            _logger.LogInformation("[{SessionId}] Sesión finalizada", _sessionId);
        }
    }

    private async Task DispatchTextMessageAsync(
        NativeWebSocket webSocket,
        string messageType,
        byte[] payload,
        CancellationToken ct)
    {
        switch (_state, messageType)
        {
            // Estado CONNECTED: solo AUTH es válido.
            case (SessionState.Connected, MessageType.Auth):
                var authMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.AuthMessage);
                if (authMsg is null) break;
                _authToken = authMsg.Token;
                _state = SessionState.Authenticated;
                _logger.LogInformation("[{SessionId}] AUTH recibido, estado -> Authenticated", _sessionId);
                await WebSocketTransport.SendJsonAsync(webSocket, new AuthOkMessage(), AcwfJsonContext.Default.AuthOkMessage, ct);
                break;

            case (SessionState.Connected, _):
                _logger.LogWarning("[{SessionId}] Se recibió {Type} antes de AUTH", _sessionId, messageType);
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, "AUTH_REQUIRED", "Authentication required before sending data", 1008, _logger, _sessionId, ct);
                return;

            // Estado AUTHENTICATED: solo PDF_DOWNLOAD es válido.
            case (SessionState.Authenticated, MessageType.PdfDownload):
                var pdfMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.PdfDownloadMessage);
                if (pdfMsg is null) break;
                _firmaHandler.SetCurrentFilename(pdfMsg.Filename);
                _state = SessionState.ReceivingFile;
                _logger.LogInformation(
                    "[{SessionId}] Metadatos PDF_DOWNLOAD recibidos: {Filename} ({Size} bytes), estado -> ReceivingFile",
                    _sessionId, pdfMsg.Filename, pdfMsg.Size);
                break;

            // Estado WATCHING: REQUEST_SIGNED_FILE es válido.
            case (SessionState.WatchingFirma, MessageType.RequestSignedFile):
                var reqMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.RequestSignedFileMessage);
                if (reqMsg is null) break;
                _state = SessionState.SendingFile;
                _logger.LogInformation("[{SessionId}] REQUEST_SIGNED_FILE recibido, estado -> SendingFile", _sessionId);
                _state = await _firmaHandler.SendSignedFileAsync(webSocket, reqMsg.Filename, ct);
                return;

            // Estado WAITING_CLEANUP_CONFIRM: CLEANUP_CONFIRMED es válido.
            case (SessionState.WaitingCleanupConfirm, MessageType.CleanupConfirmed):
                _logger.LogInformation("[{SessionId}] CLEANUP_CONFIRMED recibido, eliminando archivos", _sessionId);
                _firmaHandler.Cleanup();
                await WebSocketTransport.SendJsonAsync(webSocket, new CleanupDoneMessage(), AcwfJsonContext.Default.CleanupDoneMessage, ct);
                await WebSocketTransport.CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Cleanup complete", ct);
                _state = SessionState.Idle;
                return;

            default:
                _logger.LogWarning(
                    "[{SessionId}] Tipo de mensaje inesperado {Type} en estado {State}",
                    _sessionId, messageType, _state);
                var errorCode = IsKnownMessageType(messageType) ? "UNEXPECTED_MESSAGE" : "UNKNOWN_MESSAGE_TYPE";
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, errorCode, $"Message type {messageType} not valid in state {_state}", 1011, _logger, _sessionId, ct);
                return;
        }
    }

    private static bool IsKnownMessageType(string type)
    {
        return type is MessageType.Auth or MessageType.PdfDownload or MessageType.RequestSignedFile
            or MessageType.CleanupConfirmed
            or MessageType.Connected or MessageType.PdfReceived or MessageType.FirmaDisponible
            or MessageType.SignedFile or MessageType.FirmaTimeout or MessageType.Error
            or MessageType.CleanupDone;
    }
}