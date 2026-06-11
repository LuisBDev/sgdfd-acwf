using System.Threading.Channels;
using ACWF.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ACWF.Firma;

/// <summary>
/// Observa el WatchDirectory en busca de un PDF firmado (sufijo _F.pdf).
/// Usa FileSystemWatcher en un thread del pool, Channel&lt;FirmaEvent&gt; para
/// marshalling seguro de eventos al async session loop.
/// </summary>
public sealed class FirmaWatcherService : IFirmaWatcherService
{
    private readonly AcwfOptions _options;
    private readonly ILogger<FirmaWatcherService> _logger;

    private readonly Channel<FirmaEvent> _channel = Channel.CreateUnbounded<FirmaEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _timeoutCts;
    private string? _expectedFilename;

    public ChannelReader<FirmaEvent> Events => _channel.Reader;

    // Delays de exponential backoff: 50, 100, 200, 400, 800 ms (5 retries máximo)
    private static readonly int[] BackoffDelaysMs = [50, 100, 200, 400, 800];

    public FirmaWatcherService(
        IOptions<AcwfOptions> options,
        ILogger<FirmaWatcherService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void StartWatching(string originalFilename)
    {
        _expectedFilename = Path.GetFileNameWithoutExtension(originalFilename) + "_F.pdf";
        _logger.LogInformation("FirmaWatcher started. Expecting file: {ExpectedFile}", _expectedFilename);

        _watcher = new FileSystemWatcher(_options.WatchDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "*_F.pdf",
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;

        _timeoutCts = new CancellationTokenSource();
        var timeoutToken = _timeoutCts.Token;

        // Programar la tarea de timeout.
        _ = Task.Delay(TimeSpan.FromSeconds(_options.FirmaTimeoutSeconds), timeoutToken)
            .ContinueWith(
                t => OnTimeout(t, originalFilename),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_expectedFilename is null) return;

        if (!string.Equals(Path.GetFileName(e.FullPath), _expectedFilename, StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogInformation("Firma file event detected: {FilePath}", e.FullPath);

        // Cancelar timeout — el archivo fue detectado.
        _timeoutCts?.Cancel();

        // Retry-read en thread pool para manejar race condition con FirmaONPE todavía escribiendo.
        _ = Task.Run(() => TryReadFileWithRetryAsync(e.FullPath));
    }

    private async Task TryReadFileWithRetryAsync(string path)
    {
        for (int i = 0; i < BackoffDelaysMs.Length; i++)
        {
            try
            {
                // Intentar abrir con FileShare.Read para verificar que el archivo es accesible.
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Archivo accesible — escribir evento de éxito.
                _logger.LogInformation("Firma file is readable after {Attempt} attempt(s): {FilePath}", i + 1, path);
                await _channel.Writer.WriteAsync(new FirmaEvent(FirmaEventType.FileReady, path)).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                _logger.LogWarning(
                    "Firma file locked (attempt {Attempt}/{Max}): {FilePath}",
                    i + 1, BackoffDelaysMs.Length, path);

                if (i < BackoffDelaysMs.Length - 1)
                {
                    await Task.Delay(BackoffDelaysMs[i]).ConfigureAwait(false);
                }
            }
        }

        // Todos los retries agotados.
        _logger.LogError("Firma file still locked after all retries: {FilePath}", path);
        await _channel.Writer.WriteAsync(
            new FirmaEvent(FirmaEventType.Error, path, "FILE_LOCKED")).ConfigureAwait(false);
    }

    private void OnTimeout(Task completedTask, string originalFilename)
    {
        if (completedTask.IsCanceled)
        {
            // Archivo detectado antes del timeout — no hacer nada.
            return;
        }

        _logger.LogWarning(
            "FirmaWatcher timeout after {Seconds}s for {Filename}",
            _options.FirmaTimeoutSeconds, originalFilename);

        _channel.Writer.TryWrite(new FirmaEvent(FirmaEventType.Timeout, string.Empty));
    }

    public async ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _channel.Writer.TryComplete();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
