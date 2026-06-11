using ACWF.Configuration;
using ACWF.System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;

// Evitar ambigüedad con Velopack.UpdateOptions
using AppUpdateOptions = ACWF.Configuration.UpdateOptions;
using VelopackUpdateOptions = Velopack.UpdateOptions;

namespace ACWF.Update;

public interface IUpdateTrigger
{
    Task CheckNowAsync();
    bool HasPendingUpdate { get; }
    int LastProgress { get; }
    void ApplyUpdate();
}

/// <summary>
/// Background service que periódicamente busca updates de Velopack.
/// Descarga updates silenciosamente. No aplica sin acción explícita del usuario.
/// </summary>
public sealed class UpdateService : BackgroundService, IUpdateTrigger
{
    private readonly AppUpdateOptions _options;
    private readonly ITrayStateNotifier _trayNotifier;
    private readonly ILogger<UpdateService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private UpdateInfo? _pendingUpdate;
    private UpdateManager? _updateManager;

    public bool HasPendingUpdate => _pendingUpdate is not null;
    public int LastProgress { get; private set; }

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Esperar a que el agente se estabilice antes del primer check.
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndDownloadAsync().ConfigureAwait(false);

            await Task.Delay(
                TimeSpan.FromHours(_options.CheckIntervalHours),
                stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task CheckNowAsync()
    {
        await CheckAndDownloadAsync().ConfigureAwait(false);
    }

    private async Task CheckAndDownloadAsync()
    {
        if (string.IsNullOrEmpty(_options.RepoUrl))
        {
            _logger.LogDebug("UpdateService: RepoUrl not configured, skipping update check");
            return;
        }

        try
        {
            _logger.LogInformation("Checking for updates at {RepoUrl}", _options.RepoUrl);

            _updateManager = new UpdateManager(_options.RepoUrl, new VelopackUpdateOptions
            {
                AllowVersionDowngrade = false,
                ExplicitChannel = _options.IncludePrerelease ? "pre" : null
            });

            UpdateInfo? updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (updateInfo is null)
            {
                _logger.LogInformation("No updates available");
                return;
            }

            _logger.LogInformation(
                "Update available: {Version}",
                updateInfo.TargetFullRelease.Version);

            // Descargar en background con reporte de progreso.
            await _updateManager.DownloadUpdatesAsync(updateInfo, percent =>
            {
                LastProgress = percent;
                _trayNotifier.NotifyUpdateProgress(percent);

                if (percent is 0 or 50 or 100)
                {
                    _logger.LogInformation("Update download progress: {Percent}%", percent);
                }
            }).ConfigureAwait(false);

            _pendingUpdate = updateInfo;
            _trayNotifier.NotifyUpdateAvailable(updateInfo.TargetFullRelease.Version.ToString());

            _logger.LogInformation(
                "Update {Version} downloaded and ready to apply",
                updateInfo.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during update check/download");
        }
    }

    /// <summary>
    /// Aplica el update pendiente reiniciando el proceso vía Velopack.
    /// Solo debe llamarse cuando no hay una sesión WebSocket activa.
    /// </summary>
    public void ApplyUpdate()
    {
        if (_pendingUpdate is null || _updateManager is null)
        {
            _logger.LogWarning("ApplyUpdate called but no pending update is available");
            return;
        }

        _logger.LogInformation(
            "Applying update {Version}",
            _pendingUpdate.TargetFullRelease.Version);

        // UpdateInfo tiene una conversión implícita a VelopackAsset.
        _updateManager.WaitExitThenApplyUpdates((VelopackAsset)_pendingUpdate);
        _lifetime.StopApplication();
    }
}
