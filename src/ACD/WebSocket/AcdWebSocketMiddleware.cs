using ACWF.Configuration;
using ACWF.Firma;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;
using WebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;

namespace ACWF.WebSocket;

/// <summary>
///     Middleware de ASP.NET Core que maneja WebSocket upgrades en /acwf.
///     Aplica validación de Origin, single-session gate, y delega a AcwfSessionHandler.
/// </summary>
public sealed class AcwfWebSocketMiddleware
{
    private readonly IAcwfSessionHandlerFactory _factory;
    private readonly ILogger<AcwfWebSocketMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly AcwfOptions _options;
    private readonly ISessionGate _sessionGate;
    private readonly IServiceProvider _sp;

    public AcwfWebSocketMiddleware(
        RequestDelegate next,
        ISessionGate sessionGate,
        IOptions<AcwfOptions> options,
        ILogger<AcwfWebSocketMiddleware> logger,
        IAcwfSessionHandlerFactory factory,
        IServiceProvider sp)
    {
        _next = next;
        _sessionGate = sessionGate;
        _options = options.Value;
        _logger = logger;
        _factory = factory;
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

        // Solicitud no-WebSocket en /acwf → health check (sin session gate).
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (IsOriginAllowed(origin)) context.Response.Headers["Access-Control-Allow-Origin"] = origin;

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"ok\"}", context.RequestAborted);
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

        // Intentar adquirir el bloqueo de sesión única.
        var acquired = await _sessionGate.TryAcquireAsync(context.RequestAborted);

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
        var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();

        var sessionId = Guid.NewGuid().ToString("N");
        NativeWebSocket? webSocket = null;

        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("Sesión WebSocket abierta: {SessionId}", sessionId);

            var handler = _factory.Create(sessionId, webSocket, scope);
            await handler.HandleAsync(webSocket, context.RequestAborted);
        }
        finally
        {
            _sessionGate.Release();
            await watcherService.DisposeAsync();

            if (webSocket is not null) _logger.LogInformation("Sesión WebSocket cerrada: {SessionId}", sessionId);
        }
    }

    private bool IsOriginAllowed(string origin)
    {
        if (_options.AllowedOrigins.Length == 0) return false;
        if (_options.AllowedOrigins.Contains("*")) return true;

        foreach (var allowed in _options.AllowedOrigins)
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}