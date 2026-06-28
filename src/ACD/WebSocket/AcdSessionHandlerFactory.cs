using ACWF.Configuration;
using ACWF.Firma;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACWF.WebSocket;

/// <summary>
///     Fábrica singleton que compone AcwfSessionHandler con sus dependencias por sesión.
///     Resuelve servicios scoped (IFileDepositService, IFirmaWatcherService) desde el scope inyectado.
/// </summary>
public sealed class AcwfSessionHandlerFactory : IAcwfSessionHandlerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcwfOptions _options;

    public AcwfSessionHandlerFactory(IOptions<AcwfOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public AcwfSessionHandler Create(string sessionId, NativeWebSocket webSocket, IServiceScope scope)
    {
        var depositService = scope.ServiceProvider.GetRequiredService<IFileDepositService>();
        var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();
        var logger = _loggerFactory.CreateLogger<AcwfSessionHandler>();

        var firmaHandler = new FirmaWorkflowHandler(
            depositService,
            watcherService,
            _options.WatchDirectory,
            _options.FirmaTimeoutSeconds,
            _options.FirmaSignedSuffix,
            logger,
            sessionId);

        return new AcwfSessionHandler(firmaHandler, logger, sessionId, _options.WatchDirectory);
    }
}