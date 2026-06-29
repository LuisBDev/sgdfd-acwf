using System.Threading.Channels;
using ACD.Configuration;
using Microsoft.Extensions.Options;

namespace ACD.Firma;

/// <summary>
///     Observa el WatchDirectory en busca de un PDF firmado (sufijo [F].pdf).
///     FirmaONPE crea el archivo vacío y bloqueado, y lo completa recién cuando el
///     usuario ingresa su clave; por eso se espera a que quede completo y desbloqueado
///     hasta el timeout global, no con reintentos cortos.
/// </summary>
public sealed class FirmaWatcherService : IFirmaWatcherService
{
    private const int PollIntervalMs = 500;

    private readonly Channel<FirmaEvent> _channel = Channel.CreateUnbounded<FirmaEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ILogger<FirmaWatcherService> _logger;
    private readonly AcdOptions _options;
    private string? _expectedFilename;
    private CancellationTokenSource? _timeoutCts;
    private int _waitStarted;

    private FileSystemWatcher? _watcher;

    public FirmaWatcherService(
        IOptions<AcdOptions> options,
        ILogger<FirmaWatcherService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ChannelReader<FirmaEvent> Events => _channel.Reader;

    public void StartWatching(string originalFilename, string signedSuffix = "[F]")
    {
        _expectedFilename = Path.GetFileNameWithoutExtension(originalFilename) + signedSuffix + ".pdf";
        _waitStarted = 0;
        _logger.LogInformation("FirmaWatcher iniciado. Esperando archivo: {ExpectedFile}", _expectedFilename);

        _watcher = new FileSystemWatcher(_options.WatchDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = $"*{signedSuffix}.pdf",
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;

        _timeoutCts = new CancellationTokenSource();
        var timeoutToken = _timeoutCts.Token;

        _ = Task.Delay(TimeSpan.FromSeconds(_options.FirmaTimeoutSeconds), timeoutToken)
            .ContinueWith(
                t => OnTimeout(t, originalFilename),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
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

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_expectedFilename is null) return;

        if (!string.Equals(Path.GetFileName(e.FullPath), _expectedFilename, StringComparison.OrdinalIgnoreCase))
            return;

        // El archivo aparece vacío y bloqueado; arrancar la espera una sola vez.
        if (Interlocked.Exchange(ref _waitStarted, 1) != 0) return;

        _logger.LogInformation("Archivo de firma detectado, esperando a que se complete: {FilePath}", e.FullPath);
        _ = WaitForSignedFileAsync(e.FullPath, _timeoutCts?.Token ?? CancellationToken.None);
    }

    // Espera a que el archivo esté desbloqueado y con tamaño estable (>0) entre dos lecturas.
    private async Task WaitForSignedFileAsync(string path, CancellationToken token)
    {
        var previousLength = -1L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var readable = TryGetReadableLength(path, out var length);

                if (readable && length == previousLength)
                {
                    _timeoutCts?.Cancel();
                    _logger.LogInformation("Archivo de firma listo: {FilePath} ({Bytes} bytes)", path, length);
                    await _channel.Writer.WriteAsync(new FirmaEvent(FirmaEventType.FileReady, path)).ConfigureAwait(false);
                    return;
                }

                previousLength = readable ? length : -1;
                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout global o cierre de sesión — OnTimeout emite el evento Timeout.
        }
    }

    private static bool TryGetReadableLength(string path, out long length)
    {
        length = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = fs.Length;
            return length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnTimeout(Task completedTask, string originalFilename)
    {
        if (completedTask.IsCanceled)
            return;

        _logger.LogWarning(
            "FirmaWatcher agotó el tiempo de espera tras {Seconds}s para {Filename}",
            _options.FirmaTimeoutSeconds, originalFilename);

        _channel.Writer.TryWrite(new FirmaEvent(FirmaEventType.Timeout, string.Empty));
    }
}
