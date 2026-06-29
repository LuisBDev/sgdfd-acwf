using System.Net.WebSockets;
using ACD.Configuration;
using ACD.Firma.Signing;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.Firma;

/// <summary>
///     Orquesta el flujo de firma: depósito del frame binario, inicio del watcher,
///     consumo concurrente de eventos y envío del archivo firmado.
///     No posee las transiciones de estado — cada método retorna el siguiente SessionState
///     y el llamador (AcdSessionHandler) lo aplica.
/// </summary>
public sealed class FirmaWorkflowHandler
{
    private readonly IFileDepositService _depositService;
    private readonly IFirmaLauncher _firmaLauncher;
    private readonly FirmaOptions _firmaOptions;
    private readonly string _firmaSignedSuffix;
    private readonly int _firmaTimeoutSeconds;
    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly string _watchDirectory;
    private readonly IFirmaWatcherService _watcherService;
    private string? _firmaFilePath;
    private string? _requestedTipo;

    public FirmaWorkflowHandler(
        IFileDepositService deposit,
        IFirmaWatcherService watcher,
        IFirmaLauncher firmaLauncher,
        FirmaOptions firmaOptions,
        string watchDirectory,
        int firmaTimeoutSeconds,
        string firmaSignedSuffix,
        ILogger logger,
        string sessionId)
    {
        _depositService = deposit;
        _watcherService = watcher;
        _firmaLauncher = firmaLauncher;
        _firmaOptions = firmaOptions;
        _watchDirectory = watchDirectory;
        _firmaTimeoutSeconds = firmaTimeoutSeconds;
        _firmaSignedSuffix = firmaSignedSuffix;
        _logger = logger;
        _sessionId = sessionId;
    }

    public string? CurrentFilename { get; private set; }

    public async Task<SessionState> HandleBinaryFrameAsync(
        NativeWebSocket ws,
        byte[] data,
        CancellationToken ct)
    {
        if (CurrentFilename is null)
        {
            _logger.LogWarning("[{SessionId}] Se recibió un frame binario en estado inesperado", _sessionId);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "UNEXPECTED_MESSAGE", "Binary frame received in wrong state", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        _logger.LogInformation(
            "[{SessionId}] Recibiendo frame binario PDF para {Filename} ({Size} bytes)",
            _sessionId, CurrentFilename, data.Length);

        using var stream = new MemoryStream(data);
        string filePath;
        try
        {
            filePath = await _depositService.DepositAsync(CurrentFilename, stream, ct);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "[{SessionId}] Nombre de archivo inválido: {Filename}", _sessionId, CurrentFilename);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "INVALID_FILENAME", ex.Message, 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }
        catch (InvalidOperationException ex) when (ex.Message == "WRITE_FAILED")
        {
            _logger.LogError(ex, "[{SessionId}] Error de escritura para {Filename}", _sessionId, CurrentFilename);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "WRITE_FAILED", "Could not write PDF to watch directory", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        // Confirmar recepción y comenzar a vigilar.
        await WebSocketTransport.SendJsonAsync(ws, new PdfReceivedMessage(CurrentFilename), AcdJsonContext.Default.PdfReceivedMessage, ct);

        // El tipo de firma es crítico: sin él no se firma (validado en PDF_DOWNLOAD, guarda defensiva aquí).
        if (_requestedTipo is null)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "MISSING_FIRMA_TIPO", "Firma type is required", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        // Armar el watcher antes de lanzar, para no perder el evento del [F].pdf.
        _watcherService.StartWatching(CurrentFilename, _firmaSignedSuffix);
        _logger.LogInformation("[{SessionId}] Archivo escrito en {Path}, estado -> WatchingFirma", _sessionId, filePath);

        var firmaRequest = new FirmaRequest(
            filePath,
            _requestedTipo,
            new FirmaInstitutionalData(
                _firmaOptions.Area, _firmaOptions.Telefono, _firmaOptions.Anexo, _firmaOptions.Url));

        var launch = _firmaLauncher.Launch(firmaRequest);
        if (!launch.Success)
        {
            _logger.LogError("[{SessionId}] No se pudo lanzar el firmador: {Reason}", _sessionId, launch.ErrorMessage);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "FIRMA_LAUNCH_FAILED", launch.ErrorMessage ?? "Could not launch signer", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        // Consumir eventos del watcher en segundo plano.
        _ = WatchFirmaAsync(ws, ct);

        return SessionState.WatchingFirma;
    }

    public async Task WatchFirmaAsync(NativeWebSocket ws, CancellationToken ct)
    {
        try
        {
            await foreach (var firmaEvent in _watcherService.Events.ReadAllAsync(ct))
                switch (firmaEvent.Type)
                {
                    case FirmaEventType.FileReady:
                        _firmaFilePath = firmaEvent.FilePath;
                        var signedFilename = Path.GetFileName(firmaEvent.FilePath);
                        _logger.LogInformation("[{SessionId}] FIRMA_DISPONIBLE: {File}", _sessionId, signedFilename);
                        await WebSocketTransport.SendJsonAsync(ws, new FirmaDisponibleMessage(signedFilename), AcdJsonContext.Default.FirmaDisponibleMessage, ct);
                        // El estado WatchingFirma se mantiene hasta REQUEST_SIGNED_FILE.
                        break;

                    case FirmaEventType.Timeout:
                        _logger.LogWarning("[{SessionId}] FirmaWatcher agotó el tiempo de espera para {Filename}", _sessionId, CurrentFilename);
                        await WebSocketTransport.SendJsonAsync(ws,
                            new FirmaTimeoutMessage(CurrentFilename ?? string.Empty, _firmaTimeoutSeconds),
                            AcdJsonContext.Default.FirmaTimeoutMessage, ct);
                        await WebSocketTransport.CloseWebSocketAsync(ws, WebSocketCloseStatus.NormalClosure, "Firma timeout", ct);
                        return;

                    case FirmaEventType.Error:
                        _logger.LogError("[{SessionId}] Error de FirmaWatcher: {Error}", _sessionId, firmaEvent.ErrorMessage);
                        await WebSocketTransport.SendErrorAndCloseAsync(ws, firmaEvent.ErrorMessage ?? "FILE_LOCKED", "Signed file is locked", 1011, _logger, _sessionId, ct);
                        return;
                }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[{SessionId}] WatchFirmaAsync cancelado", _sessionId);
        }
    }

    public async Task<SessionState> SendSignedFileAsync(
        NativeWebSocket ws,
        string signedFilename,
        CancellationToken ct)
    {
        // Ruta completa capturada por WatchFirmaAsync; fallback desde watch dir.
        var filePath = _firmaFilePath
                       ?? Path.Combine(_watchDirectory, signedFilename);

        if (!File.Exists(filePath))
        {
            _logger.LogError("[{SessionId}] Archivo firmado no encontrado: {FilePath}", _sessionId, filePath);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "READ_FAILED", $"Signed file not found: {signedFilename}", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(filePath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] No se puede obtener información del archivo firmado: {FilePath}", _sessionId, filePath);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "READ_FAILED", "Cannot read signed file metadata", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        // Frame 1: metadatos.
        await WebSocketTransport.SendJsonAsync(ws,
            new SignedFileMessage(Path.GetFileName(filePath), fileSize),
            AcdJsonContext.Default.SignedFileMessage, ct);

        // Frame 2: contenido del archivo en fragmentos de 64 KB.
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, true);

            var buffer = new byte[64 * 1024];
            var remaining = fileSize;
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                remaining -= bytesRead;
                var endOfMessage = remaining <= 0;
                await ws.SendAsync(
                    buffer.AsMemory(0, bytesRead),
                    WebSocketMessageType.Binary,
                    endOfMessage,
                    ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[{SessionId}] SIGNED_FILE enviado: {Filename} ({Size} bytes)",
                _sessionId, Path.GetFileName(filePath), fileSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Error al enviar el archivo firmado: {FilePath}", _sessionId, filePath);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, "READ_FAILED", "Error reading signed file", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        _logger.LogInformation("[{SessionId}] SIGNED_FILE enviado, esperando CLEANUP_CONFIRMED", _sessionId);
        return SessionState.WaitingCleanupConfirm;
    }

    public void SetCurrentFilename(string filename, string? tipo = null)
    {
        CurrentFilename = filename;
        _requestedTipo = tipo;
    }

    public void Cleanup()
    {
        if (CurrentFilename is not null)
            _depositService.Cleanup(CurrentFilename, _firmaSignedSuffix);
    }
}