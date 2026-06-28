using System.Diagnostics;
using System.Reflection;
using ACWF.Configuration;
using ACWF.Update;
using Microsoft.Extensions.Options;

namespace ACWF.Tray;

/// <summary>
///     Aloja el NotifyIcon en un thread STA dedicado.
///     Implementa IHostedService para integrarse con Generic Host lifetime.
///     Implementa ITrayStateNotifier para que otros servicios puedan actualizar el estado del icono.
/// </summary>
public sealed class TrayIconService : IHostedService, ITrayStateNotifier, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TrayIconService> _logger;
    private readonly IOptions<AcwfOptions> _options;

    private readonly TaskCompletionSource _staReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<IUpdateTrigger> _updateTrigger;

    private TrayState _currentState = TrayState.Ready;
    private NotifyIcon? _notifyIcon;
    private string? _pendingUpdateVersion;

    private Thread? _staThread;
    private ToolStripMenuItem? _statusItem;
    private SynchronizationContext? _syncContext;
    private ToolStripMenuItem? _updateItem;
    private int _updateProgress;

    public TrayIconService(
        IHostApplicationLifetime lifetime,
        IOptions<AcwfOptions> options,
        ILogger<TrayIconService> logger,
        Lazy<IUpdateTrigger> updateTrigger)
    {
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
        _updateTrigger = updateTrigger;
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _staThread = new Thread(RunSta)
        {
            IsBackground = true,
            Name = "TrayIconSTA"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        return _staReady.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_syncContext is null) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _syncContext.Post(_ =>
        {
            try
            {
                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                Application.ExitThread();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al eliminar el icono de la bandeja");
            }
            finally
            {
                tcs.TrySetResult();
            }
        }, null);

        _staThread?.Join(TimeSpan.FromSeconds(3));
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public void SetState(TrayState state)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _currentState = state;
            if (_notifyIcon is not null) _notifyIcon.Icon = CreateIcon(state);
            if (_statusItem is not null) _statusItem.Text = $"Status: {state}";
        }, null);
    }

    public void NotifyUpdateAvailable(string version)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _pendingUpdateVersion = version;
            if (_updateItem is not null) _updateItem.Text = $"Update ready: {version}";
            _notifyIcon?.ShowBalloonTip(
                5000,
                "ACWF Update Ready",
                $"Version {version} is ready to install.",
                ToolTipIcon.Info);
        }, null);
    }

    public void NotifyUpdateProgress(int percent)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _updateProgress = percent;
            if (_updateItem is not null)
                _updateItem.Text = percent < 100
                    ? $"Downloading update... {percent}%"
                    : "Update download complete";
        }, null);
    }

    private void RunSta()
    {
        WindowsFormsSynchronizationContext.AutoInstall = false;
        var ctx = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        _syncContext = ctx;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

        _notifyIcon = new NotifyIcon
        {
            Text = "ACWF",
            Visible = true,
            Icon = CreateIcon(TrayState.Ready)
        };

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Status: Ready") { Enabled = false };
        menu.Items.Add(_statusItem);

        var versionItem = new ToolStripMenuItem($"Version: {version}") { Enabled = false };
        menu.Items.Add(versionItem);

        menu.Items.Add(new ToolStripSeparator());

        _updateItem = new ToolStripMenuItem("Check for updates");
        _updateItem.Click += (_, _) => CheckForUpdatesClicked();
        menu.Items.Add(_updateItem);

        menu.Items.Add(new ToolStripSeparator());

        var restartItem = new ToolStripMenuItem("Restart");
        restartItem.Click += (_, _) => RestartClicked();
        menu.Items.Add(restartItem);

        var closeItem = new ToolStripMenuItem("Close");
        closeItem.Click += (_, _) => _lifetime.StopApplication();
        menu.Items.Add(closeItem);

        _notifyIcon.ContextMenuStrip = menu;

        _staReady.TrySetResult();

        Application.Run(new ApplicationContext());
    }

    private async void CheckForUpdatesClicked()
    {
        _logger.LogInformation("El usuario solicitó verificación manual de actualizaciones");
        try
        {
            await _updateTrigger.Value.CheckNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la verificación manual de actualizaciones");
        }
    }

    private void RestartClicked()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath)) Process.Start(exePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al reiniciar el proceso");
        }

        _lifetime.StopApplication();
    }

    private static Icon CreateIcon(TrayState state)
    {
        var color = state switch
        {
            TrayState.Ready => Color.Green,
            TrayState.Connected => Color.Blue,
            TrayState.Error => Color.Red,
            _ => Color.Gray
        };

        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);

        var hIcon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        return icon;
    }
}