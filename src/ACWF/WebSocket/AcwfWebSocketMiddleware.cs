using System.Net.WebSockets;
using ACWF.Configuration;
using ACWF.Firma;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;
using WebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;

namespace ACWF.WebSocket;

/// <summary>
/// Middleware de ASP.NET Core que maneja WebSocket upgrades en /acwf.
/// Aplica validación de Origin, single-session gate, y delega a AcwfSessionHandler.
/// </summary>
public sealed class AcwfWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISessionGate _sessionGate;
    private readonly AcwfOptions _options;
    private readonly ILogger<AcwfWebSocketMiddleware> _logger;
    private readonly IServiceProvider _sp;

    public AcwfWebSocketMiddleware(
        RequestDelegate next,
        ISessionGate sessionGate,
        IOptions<AcwfOptions> options,
        ILogger<AcwfWebSocketMiddleware> logger,
        IServiceProvider sp)
    {
        _next = next;
        _sessionGate = sessionGate;
        _options = options.Value;
        _logger = logger;
        _sp = sp;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Solo manejar requests al endpoint /acwf.
        if (!context.Request.Path.Equals("/acwf", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Debe ser un WebSocket upgrade request.
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Validar header Origin.
        if (!IsOriginAllowed(context.Request.Headers.Origin.ToString()))
        {
            _logger.LogWarning(
                "Upgrade WebSocket rechazado — el Origin '{Origin}' no está en AllowedOrigins",
                context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Intentar adquirir el single-session gate.
        bool acquired = await _sessionGate.TryAcquireAsync(context.RequestAborted);

        if (!acquired)
        {
            _logger.LogWarning("Upgrade WebSocket rechazado — sesión ya activa (4002)");
            // Debe aceptar el upgrade antes de enviar el close frame.
            var busyWs = await context.WebSockets.AcceptWebSocketAsync();
            await busyWs.CloseAsync(
                (WebSocketCloseStatus)4002,
                "Session already active",
                context.RequestAborted);
            return;
        }

        // Crear un DI scope para esta sesión (scoped services: FileDepositService, FirmaWatcherService).
        await using var scope = _sp.CreateAsyncScope();
        var depositService = scope.ServiceProvider.GetRequiredService<IFileDepositService>();
        var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();

        string sessionId = Guid.NewGuid().ToString("N");
        NativeWebSocket? webSocket = null;

        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("Sesión WebSocket abierta: {SessionId}", sessionId);

            var handler = new AcwfSessionHandler(depositService, watcherService, _logger, sessionId, _options.WatchDirectory, _options.FirmaTimeoutSeconds);
            await handler.HandleAsync(webSocket, context.RequestAborted);
        }
        finally
        {
            _sessionGate.Release();
            await watcherService.DisposeAsync();

            if (webSocket is not null)
            {
                _logger.LogInformation("Sesión WebSocket cerrada: {SessionId}", sessionId);
            }
        }
    }

    private bool IsOriginAllowed(string origin)
    {
        if (_options.AllowedOrigins.Length == 0) return false;

        foreach (var allowed in _options.AllowedOrigins)
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
