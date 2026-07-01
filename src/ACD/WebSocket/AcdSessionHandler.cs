using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

/// <summary>
///     Maneja el ciclo de vida completo de una sesión WebSocket vía una state machine.
///     Se instancia por sesión desde AcdWebSocketMiddleware a través de IAcdSessionHandlerFactory.
/// </summary>
public sealed class AcdSessionHandler
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly FirmaWorkflowHandler _firmaHandler;
    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly string _watchDirectory;
    private string? _authToken;

    private SessionState _state = SessionState.Connected;

    public AcdSessionHandler(
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
            await WebSocketTransport.SendJsonAsync(webSocket, connected, AcdJsonContext.Default.ConnectedMessage, ct);

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
                        await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.UnexpectedMessage, "Binary frame received in wrong state", 1011, _logger, _sessionId, ct);
                        return;
                    }

                    _state = await _firmaHandler.HandleBinaryFrameAsync(webSocket, payload!, ct);
                    continue;
                }

                // Text frame — deserializar y despachar.
                if (payload is null) continue;

                var baseMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.BaseMessage);
                if (baseMsg is null)
                {
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.UnknownMessageType, "Could not parse message type", 1011, _logger, _sessionId, ct);
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
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.InternalError, ex.Message, 1011, _logger, _sessionId, ct);
            }
            catch
            {
                /* Limpieza al finalizar */
            }
        }
        finally
        {
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
                var authMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.AuthMessage);
                if (authMsg is null) break;
                _authToken = authMsg.Token;
                _state = SessionState.Authenticated;
                _logger.LogInformation("[{SessionId}] AUTH recibido, estado -> Authenticated", _sessionId);
                await WebSocketTransport.SendJsonAsync(webSocket, new AuthOkMessage(), AcdJsonContext.Default.AuthOkMessage, ct);
                break;

            case (SessionState.Connected, _):
                _logger.LogWarning("[{SessionId}] Se recibió {Type} antes de AUTH", _sessionId, messageType);
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.AuthRequired, "Authentication required before sending data", 1008, _logger, _sessionId, ct);
                return;

            // Estado AUTHENTICATED: solo PDF_DOWNLOAD es válido.
            case (SessionState.Authenticated, MessageType.PdfDownload):
                var pdfMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.PdfDownloadMessage);
                if (pdfMsg is null) break;
                if (!FirmaTipo.IsSupported(pdfMsg.Tipo))
                {
                    var code = string.IsNullOrEmpty(pdfMsg.Tipo) ? ErrorCatalog.MissingFirmaTipo : ErrorCatalog.UnsupportedFirmaTipo;
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, code, $"Invalid firma type: {pdfMsg.Tipo ?? "(none)"}", 1011, _logger, _sessionId, ct);
                    return;
                }

                _firmaHandler.SetCurrentFilename(pdfMsg.Filename, pdfMsg.Tipo);
                _state = SessionState.ReceivingFile;
                _logger.LogInformation(
                    "[{SessionId}] Metadatos PDF_DOWNLOAD recibidos: {Filename} ({Size} bytes, tipo {Tipo}), estado -> ReceivingFile",
                    _sessionId, pdfMsg.Filename, pdfMsg.Size, pdfMsg.Tipo);
                break;

            // Estado WATCHING: REQUEST_SIGNED_FILE es válido.
            case (SessionState.WatchingFirma, MessageType.RequestSignedFile):
                var reqMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.RequestSignedFileMessage);
                if (reqMsg is null) break;
                _state = SessionState.SendingFile;
                _logger.LogInformation("[{SessionId}] REQUEST_SIGNED_FILE recibido, estado -> SendingFile", _sessionId);
                _state = await _firmaHandler.SendSignedFileAsync(webSocket, reqMsg.Filename, ct);
                return;

            default:
                _logger.LogWarning(
                    "[{SessionId}] Tipo de mensaje inesperado {Type} en estado {State}",
                    _sessionId, messageType, _state);
                var errorCode = IsKnownMessageType(messageType) ? ErrorCatalog.UnexpectedMessage : ErrorCatalog.UnknownMessageType;
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, errorCode, $"Message type {messageType} not valid in state {_state}", 1011, _logger, _sessionId, ct);
                return;
        }
    }

    private static bool IsKnownMessageType(string type)
    {
        return type is MessageType.Auth or MessageType.PdfDownload or MessageType.RequestSignedFile
            or MessageType.Connected or MessageType.PdfReceived or MessageType.FirmaDisponible
            or MessageType.SignedFile or MessageType.FirmaTimeout or MessageType.Error;
    }
}