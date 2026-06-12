using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using ACWF.Firma;
using ACWF.WebSocket.Messages;
using Microsoft.Extensions.Logging;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACWF.WebSocket;

/// <summary>
/// Maneja el ciclo de vida completo de una sesión WebSocket vía una state machine.
/// Se instancia por sesión desde AcwfWebSocketMiddleware.
/// </summary>
public sealed class AcwfSessionHandler
{
    private readonly IFileDepositService _depositService;
    private readonly IFirmaWatcherService _watcherService;
    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly string _watchDirectory;
    private readonly int _firmaTimeoutSeconds;

    private SessionState _state = SessionState.Connected;
    private string? _authToken;
    private string? _currentFilename;
    private string? _firmaFilePath;

    private const int BufferSize = 64 * 1024;

    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public AcwfSessionHandler(
        IFileDepositService depositService,
        IFirmaWatcherService watcherService,
        ILogger logger,
        string sessionId,
        string watchDirectory,
        int firmaTimeoutSeconds)
    {
        _depositService = depositService;
        _watcherService = watcherService;
        _logger = logger;
        _sessionId = sessionId;
        _watchDirectory = watchDirectory;
        _firmaTimeoutSeconds = firmaTimeoutSeconds;
    }

    public async Task HandleAsync(NativeWebSocket webSocket, CancellationToken ct)
    {
        _logger.LogInformation("[{SessionId}] Sesión iniciada", _sessionId);

        try
        {
            // Paso 1: Enviar CONNECTED inmediatamente.
            var connected = new ConnectedMessage(
                Version: AgentVersion,
                Status: "READY",
                WatchDir: _watchDirectory);
            await SendJsonAsync(webSocket, connected, AcwfJsonContext.Default.ConnectedMessage, ct);

            // Paso 2: Loop de state machine.
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (kind, payload) = await ReceiveFrameAsync(webSocket, ct);

                if (kind == FrameKind.Close)
                {
                    _logger.LogInformation("[{SessionId}] El cliente cerró la conexión", _sessionId);
                    break;
                }

                if (kind == FrameKind.Binary)
                {
                    await HandleBinaryFrameAsync(webSocket, payload!, ct);
                    continue;
                }

                // Text frame — deserializar y despachar.
                if (payload is null) continue;

                BaseMessage? baseMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.BaseMessage);
                if (baseMsg is null)
                {
                    await SendErrorAndCloseAsync(webSocket, "UNKNOWN_MESSAGE_TYPE", "Could not parse message type", 1011, ct);
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
                await SendErrorAndCloseAsync(webSocket, "INTERNAL_ERROR", ex.Message, 1011, ct);
            }
            catch { /* Limpieza best-effort */ }
        }
        finally
        {
            _state = SessionState.Closed;
            _logger.LogInformation("[{SessionId}] Sesión finalizada, estado final: {State}", _sessionId, _state);
        }
    }

    private async Task DispatchTextMessageAsync(
        NativeWebSocket webSocket,
        string messageType,
        byte[] payload,
        CancellationToken ct)
    {
        switch ((_state, messageType))
        {
            // Estado CONNECTED: solo AUTH es válido.
            case (SessionState.Connected, MessageType.Auth):
                var authMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.AuthMessage);
                if (authMsg is null) break;
                _authToken = authMsg.Token;
                _state = SessionState.Authenticated;
                _logger.LogInformation("[{SessionId}] AUTH recibido, estado -> Authenticated", _sessionId);
                await SendJsonAsync(webSocket, new AuthOkMessage(), AcwfJsonContext.Default.AuthOkMessage, ct);
                break;

            case (SessionState.Connected, _):
                _logger.LogWarning("[{SessionId}] Se recibió {Type} antes de AUTH", _sessionId, messageType);
                await SendErrorAndCloseAsync(webSocket, "AUTH_REQUIRED", "Authentication required before sending data", 1008, ct);
                return;

            // Estado AUTHENTICATED: solo PDF_DOWNLOAD es válido.
            case (SessionState.Authenticated, MessageType.PdfDownload):
                var pdfMsg = JsonSerializer.Deserialize(payload, AcwfJsonContext.Default.PdfDownloadMessage);
                if (pdfMsg is null) break;
                _currentFilename = pdfMsg.Filename;
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
                await SendSignedFileAsync(webSocket, reqMsg.Filename, ct);
                return;

            default:
                _logger.LogWarning(
                    "[{SessionId}] Tipo de mensaje inesperado {Type} en estado {State}",
                    _sessionId, messageType, _state);
                string errorCode = IsKnownMessageType(messageType) ? "UNEXPECTED_MESSAGE" : "UNKNOWN_MESSAGE_TYPE";
                await SendErrorAndCloseAsync(webSocket, errorCode, $"Message type {messageType} not valid in state {_state}", 1011, ct);
                return;
        }
    }

    private async Task HandleBinaryFrameAsync(
        NativeWebSocket webSocket,
        byte[] data,
        CancellationToken ct)
    {
        if (_state != SessionState.ReceivingFile || _currentFilename is null)
        {
            _logger.LogWarning("[{SessionId}] Se recibió un frame binario en estado inesperado {State}", _sessionId, _state);
            await SendErrorAndCloseAsync(webSocket, "UNEXPECTED_MESSAGE", "Binary frame received in wrong state", 1011, ct);
            return;
        }

        _logger.LogInformation(
            "[{SessionId}] Recibiendo frame binario PDF para {Filename} ({Size} bytes)",
            _sessionId, _currentFilename, data.Length);

        using var stream = new MemoryStream(data);
        string filePath;
        try
        {
            filePath = await _depositService.DepositAsync(_currentFilename, stream, ct);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "[{SessionId}] Nombre de archivo inválido: {Filename}", _sessionId, _currentFilename);
            await SendErrorAndCloseAsync(webSocket, "INVALID_FILENAME", ex.Message, 1011, ct);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message == "WRITE_FAILED")
        {
            _logger.LogError(ex, "[{SessionId}] Error de escritura para {Filename}", _sessionId, _currentFilename);
            await SendErrorAndCloseAsync(webSocket, "WRITE_FAILED", "Could not write PDF to watch directory", 1011, ct);
            return;
        }

        // Archivo escrito — enviar confirmación y empezar a watch.
        await SendJsonAsync(webSocket, new PdfReceivedMessage(_currentFilename), AcwfJsonContext.Default.PdfReceivedMessage, ct);

        _watcherService.StartWatching(_currentFilename);
        _state = SessionState.WatchingFirma;
        _logger.LogInformation("[{SessionId}] Archivo escrito en {Path}, estado -> WatchingFirma", _sessionId, filePath);

        // Lanzar el watcher consumer concurrentemente (no bloquea el receive loop).
        _ = WatchFirmaAsync(webSocket, ct);
    }

    private async Task WatchFirmaAsync(NativeWebSocket webSocket, CancellationToken ct)
    {
        try
        {
            await foreach (var firmaEvent in _watcherService.Events.ReadAllAsync(ct))
            {
                switch (firmaEvent.Type)
                {
                    case FirmaEventType.FileReady:
                        _firmaFilePath = firmaEvent.FilePath;
                        string signedFilename = Path.GetFileName(firmaEvent.FilePath);
                        _logger.LogInformation("[{SessionId}] FIRMA_DISPONIBLE: {File}", _sessionId, signedFilename);
                        await SendJsonAsync(webSocket, new FirmaDisponibleMessage(signedFilename), AcwfJsonContext.Default.FirmaDisponibleMessage, ct);
                        // El estado sigue WatchingFirma hasta que se recibe REQUEST_SIGNED_FILE.
                        break;

                    case FirmaEventType.Timeout:
                        _logger.LogWarning("[{SessionId}] FirmaWatcher agotó el tiempo de espera para {Filename}", _sessionId, _currentFilename);
                        await SendJsonAsync(webSocket,
                            new FirmaTimeoutMessage(_currentFilename ?? string.Empty, _firmaTimeoutSeconds),
                            AcwfJsonContext.Default.FirmaTimeoutMessage, ct);
                        await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Firma timeout", ct);
                        _state = SessionState.Idle;
                        return;

                    case FirmaEventType.Error:
                        _logger.LogError("[{SessionId}] Error de FirmaWatcher: {Error}", _sessionId, firmaEvent.ErrorMessage);
                        await SendErrorAndCloseAsync(webSocket, firmaEvent.ErrorMessage ?? "FILE_LOCKED", "Signed file is locked", 1011, ct);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[{SessionId}] WatchFirmaAsync cancelado", _sessionId);
        }
    }

    private async Task SendSignedFileAsync(
        NativeWebSocket webSocket,
        string signedFilename,
        CancellationToken ct)
    {
        // Preferir la ruta completa capturada por WatchFirmaAsync; fallback a construir desde watch dir.
        string filePath = _firmaFilePath
            ?? Path.Combine(_watchDirectory, signedFilename);

        if (!File.Exists(filePath))
        {
            _logger.LogError("[{SessionId}] Archivo firmado no encontrado: {FilePath}", _sessionId, filePath);
            await SendErrorAndCloseAsync(webSocket, "READ_FAILED", $"Signed file not found: {signedFilename}", 1011, ct);
            return;
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(filePath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] No se puede obtener información del archivo firmado: {FilePath}", _sessionId, filePath);
            await SendErrorAndCloseAsync(webSocket, "READ_FAILED", "Cannot read signed file metadata", 1011, ct);
            return;
        }

        // Frame 1: metadata JSON.
        await SendJsonAsync(webSocket,
            new SignedFileMessage(Path.GetFileName(filePath), fileSize),
            AcwfJsonContext.Default.SignedFileMessage, ct);

        // Frame 2: contenido binario del archivo en chunks de 64KB.
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long remaining = fileSize;
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                remaining -= bytesRead;
                bool endOfMessage = remaining <= 0;
                await webSocket.SendAsync(
                    buffer.AsMemory(0, bytesRead),
                    WebSocketMessageType.Binary,
                    endOfMessage: endOfMessage,
                    ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[{SessionId}] SIGNED_FILE enviado: {Filename} ({Size} bytes)",
                _sessionId, Path.GetFileName(filePath), fileSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Error al enviar el archivo firmado: {FilePath}", _sessionId, filePath);
            await SendErrorAndCloseAsync(webSocket, "READ_FAILED", "Error reading signed file", 1011, ct);
            return;
        }

        // Cierre normal después del envío exitoso.
        await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Signing session complete", ct);
        _state = SessionState.Idle;
    }

    // --- Helpers ---

    private async Task<(FrameKind Kind, byte[]? Payload)> ReceiveFrameAsync(
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
        }
        while (!result.EndOfMessage);

        var data = ms.ToArray();
        var kind = result.MessageType == WebSocketMessageType.Binary ? FrameKind.Binary : FrameKind.Text;
        return (kind, data);
    }

    private static async Task SendJsonAsync<T>(
        NativeWebSocket webSocket,
        T message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        await webSocket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task SendErrorAndCloseAsync(
        NativeWebSocket webSocket,
        string code,
        string message,
        int closeCode,
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
            _logger.LogWarning(ex, "[{SessionId}] Error al enviar el frame de error", _sessionId);
        }
    }

    private static async Task CloseWebSocketAsync(
        NativeWebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken ct)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await webSocket.CloseAsync(status, description, ct).ConfigureAwait(false);
        }
    }

    private static bool IsKnownMessageType(string type) =>
        type is MessageType.Auth or MessageType.PdfDownload or MessageType.RequestSignedFile
            or MessageType.Connected or MessageType.PdfReceived or MessageType.FirmaDisponible
            or MessageType.SignedFile or MessageType.FirmaTimeout or MessageType.Error;

    private enum FrameKind { Text, Binary, Close }
}
