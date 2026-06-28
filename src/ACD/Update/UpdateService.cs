using ACWF.Tray;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;
// Evitar ambigüedad con Velopack.UpdateOptions
using AppUpdateOptions = ACWF.Configuration.UpdateOptions;
using VelopackUpdateOptions = Velopack.UpdateOptions;

namespace ACWF.Update;

/// <summary>
///     Background service que periódicamente busca updates de Velopack.
///     Descarga updates silenciosamente. No aplica sin acción explícita del usuario.
/// </summary>
public sealed class UpdateService : BackgroundService, IUpdateTrigger
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _logger;
    private readonly AppUpdateOptions _options;
    private readonly ITrayStateNotifier _trayNotifier;

    private UpdateInfo? _pendingUpdate;
    private UpdateManager? _updateManager;

    public UpdateService(
        IOptions<AppUpdateOptions> options,
        ITrayStateNotifier trayNotifier,
        ILogger<UpdateService> logger,
        IHostApplicationLifetime lifetime)
    {
        _options = options.Value;
        _trayNotifier = trayNotifier;
        _logger = logger;
        _lifetime = lifetime;
    }

    public bool HasPendingUpdate => _pendingUpdate is not null;
    public int LastProgress { get; private set; }

    public async Task CheckNowAsync()
    {
        await CheckAndDownloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Aplica el update pendiente reiniciando el proceso vía Velopack.
    ///     Solo debe llamarse cuando no hay una sesión WebSocket activa.
    /// </summary>
    public void ApplyUpdate()
    {
        if (_pendingUpdate is null || _updateManager is null)
        {
            _logger.LogWarning("ApplyUpdate llamado pero no hay actualización pendiente disponible");
            return;
        }

        _logger.LogInformation(
            "Aplicando actualización {Version}",
            _pendingUpdate.TargetFullRelease.Version);

        // UpdateInfo tiene una conversión implícita a VelopackAsset.
        _updateManager.WaitExitThenApplyUpdates((VelopackAsset)_pendingUpdate);
        _lifetime.StopApplication();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Esperar estabilización antes de la primera verificación.
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndDownloadAsync().ConfigureAwait(false);

            await Task.Delay(
                TimeSpan.FromHours(_options.CheckIntervalHours),
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAndDownloadAsync()
    {
        if (string.IsNullOrEmpty(_options.RepoUrl))
        {
            _logger.LogDebug("UpdateService: RepoUrl no configurado, omitiendo verificación de actualizaciones");
            return;
        }

        try
        {
            _logger.LogInformation(
                "Verificando actualizaciones en {RepoUrl} (canal={Channel}, prerelease={Pre})",
                _options.RepoUrl, _options.Channel, _options.IncludePrerelease);

            // Fuente GitHub Releases: consulta la API pública del repositorio.
            // prerelease=true permite que la variante dev incluya versiones pre-release.
            var source = new GithubSource(
                _options.RepoUrl,
                string.IsNullOrWhiteSpace(_options.AccessToken) ? null : _options.AccessToken,
                _options.IncludePrerelease);

            _updateManager = new UpdateManager(source, new VelopackUpdateOptions
            {
                AllowVersionDowngrade = false,
                // El canal debe coincidir con el --channel usado en CI (stable / dev).
                ExplicitChannel = string.IsNullOrWhiteSpace(_options.Channel) ? null : _options.Channel
            });

            var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (updateInfo is null)
            {
                _logger.LogInformation("No hay actualizaciones disponibles");
                return;
            }

            _logger.LogInformation(
                "Actualización disponible: {Version}",
                updateInfo.TargetFullRelease.Version);

            // Descargar en background con reporte de progreso.
            await _updateManager.DownloadUpdatesAsync(updateInfo, percent =>
            {
                LastProgress = percent;
                _trayNotifier.NotifyUpdateProgress(percent);

                if (percent is 0 or 50 or 100) _logger.LogInformation("Progreso de descarga de actualización: {Percent}%", percent);
            }).ConfigureAwait(false);

            _pendingUpdate = updateInfo;
            _trayNotifier.NotifyUpdateAvailable(updateInfo.TargetFullRelease.Version.ToString());

            _logger.LogInformation(
                "Actualización {Version} descargada y lista para aplicar",
                updateInfo.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("La verificación de actualizaciones aún no está disponible: {Message}", ex.Message);
        }
    }
}